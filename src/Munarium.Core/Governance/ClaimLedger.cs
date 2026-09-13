namespace Munarium.Governance;

using Munarium.Commands;
using Munarium.Facts;
using Munarium.Ledger;
using Munarium.Shapes;

/// <summary>
/// The kernel's write path for claims: judge first, then one atomic conditional append, retried
/// against a moving head.
/// </summary>
/// <remarks>
/// Governance is a property of this path, not a service a caller can skip. A blocked claim is still
/// written - as disputed - so the ledger carries both the claim and the refusal.
/// <para>
/// The claim's lineage is derived here, from the shape's identity fields over the body, rather than
/// being accepted from the caller: supersession is a property of the shape, so it cannot be got
/// wrong by whoever writes the claim.
/// </para>
/// </remarks>
public sealed class ClaimLedger
{
    private readonly IStorageBackend _storage;
    private readonly ShapeRegistry _shapes;
    private readonly IReadOnlyList<IClaimGate> _gates;
    private readonly int _maxAttempts;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimLedger"/> class.
    /// </summary>
    /// <param name="storage">The ledger's storage seam.</param>
    /// <param name="shapes">The shapes claim lineage and validation are read from.</param>
    /// <param name="gates">The governance gates, evaluated in order.</param>
    /// <param name="maxAttempts">How many times a contended append is retried against the fresh head.</param>
    public ClaimLedger(
        IStorageBackend storage,
        ShapeRegistry shapes,
        IEnumerable<IClaimGate> gates,
        int maxAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(gates);

        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
        _gates = [.. gates];
        _maxAttempts = maxAttempts > 0
            ? maxAttempts
            : throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be positive.");
    }

    /// <summary>
    /// Evaluates every gate for a claim. The first <see cref="Blocked"/> verdict wins.
    /// </summary>
    /// <param name="command">The claim being recorded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verdict.</returns>
    public async ValueTask<ClaimVerdict> JudgeAsync(RecordClaimCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (var gate in _gates)
        {
            var verdict = await gate.EvaluateAsync(command, cancellationToken).ConfigureAwait(false);
            if (verdict is Blocked blocked)
            {
                return blocked;
            }
        }

        return Permitted.Instance;
    }

    /// <summary>
    /// Records a claim: judges it, then appends the matching ledger event, retrying the conditional
    /// append when another writer moved the head first.
    /// </summary>
    /// <param name="command">The claim being recorded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async ValueTask<ClaimOutcome> RecordAsync(RecordClaimCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var verdict = await JudgeAsync(command, cancellationToken).ConfigureAwait(false);
        var stream = StreamId.From(command.VersionId);
        var entry = EntryFor(command, verdict, _shapes.LineageOf(command.Shape, command.Body));
        var expected = await _storage.HeadAsync(stream, cancellationToken).ConfigureAwait(false);
        var lastExpected = expected;
        var lastActual = expected;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var result = await _storage.AppendAsync(stream, expected, [entry], cancellationToken).ConfigureAwait(false);

            if (result is Appended appended)
            {
                return verdict switch
                {
                    Permitted => new ClaimAsserted(appended.Head),
                    Blocked blocked => new ClaimRecordedAsDisputed(blocked.Gate, blocked.Reason, appended.Head),
                };
            }

            if (result is VersionConflict conflict)
            {
                lastExpected = conflict.Expected;
                lastActual = conflict.Actual;
                expected = conflict.Actual;
            }
        }

        return new ClaimContended(lastExpected, lastActual);
    }

    private static LedgerEvent EntryFor(RecordClaimCommand command, ClaimVerdict verdict, string lineage)
    {
        var (gate, reason) = verdict switch
        {
            Permitted => (string.Empty, string.Empty),
            Blocked blocked => (blocked.Gate, blocked.Reason),
        };

        var fact = new FactRecord
        {
            ClaimId = command.ClaimId,
            VersionId = command.VersionId,
            ClaimType = command.ClaimType,
            Lineage = lineage,
            Body = command.Body,
            Statement = command.Statement,
            Actor = command.Actor,
            Gate = gate,
            Reason = reason,
        };

        return new LedgerEvent(
            fact.IsDisputed ? FactCodec.DisputedEventType : FactCodec.AssertedEventType,
            FactCodec.Encode(fact));
    }
}
