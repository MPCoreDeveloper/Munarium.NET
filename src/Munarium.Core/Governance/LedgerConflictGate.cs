namespace Munarium.Governance;

using Munarium.Commands;
using Munarium.Facts;
using Munarium.Shapes;

/// <summary>
/// Refuses a claim that would overwrite what the ledger already holds on its lineage without saying so.
/// </summary>
/// <remarks>
/// A claim typed as a fact asserts a value the ledger should not already hold, so a lineage that already
/// carries an accepted fact makes it a conflict. An update or a correction says in its type why it is
/// superseding, so it is allowed - which is what keeps "the value changed" and "the earlier value was
/// wrong" distinguishable instead of being one silent write.
/// <para>
/// A disputed fact does not hold a lineage, so it does not conflict: the refusal is in the ledger, and the
/// value it refused was never established.
/// </para>
/// <para>
/// This gate reads the present state, so it costs a read of the feed on the write path. Nothing in this
/// kernel is cached; an indexed read is what would make that cheap.
/// </para>
/// </remarks>
/// <param name="shapes">Where the claim's lineage is derived from.</param>
/// <param name="facts">The read model the present state is read from.</param>
public sealed class LedgerConflictGate(ShapeRegistry shapes, FactLedger facts) : IClaimGate
{
    /// <summary>The name recorded with a claim this gate refuses.</summary>
    public const string GateName = "ledger-conflict";

    private readonly ShapeRegistry _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
    private readonly FactLedger _facts = facts ?? throw new ArgumentNullException(nameof(facts));

    /// <inheritdoc />
    public string Name => GateName;

    /// <inheritdoc />
    public async ValueTask<ClaimVerdict> EvaluateAsync(
        RecordClaimCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Only a claim that means "this is how it is" can conflict. Update and correction both say why they
        // are superseding, so they are the caller being explicit rather than silent.
        if (command.ClaimType is ClaimType.Update or ClaimType.Correction)
        {
            return Permitted.Instance;
        }

        var lineage = _shapes.LineageOf(command.Shape, command.Body);
        var pin = await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false);
        var slice = await _facts.SliceAsync(pin, cancellationToken).ConfigureAwait(false);

        // The fact that holds the lineage and is not this claim: a retry of the same claim is not a conflict.
        // "The same claim" has to mean the same content, though - keying it on the identity alone would make
        // reusing an identity a way to overwrite silently, which is the one thing this gate exists to stop.
        // Idempotency keys are what will do this properly; until then this is the honest approximation.
        var holder = slice
            .Facts
            .Select(static sliced => sliced.Fact)
            .FirstOrDefault(fact =>
                !fact.IsDisputed &&
                string.Equals(fact.Lineage, lineage, StringComparison.Ordinal) &&
                !IsTheSameClaim(fact, command));

        return holder is null
            ? Permitted.Instance
            : new Blocked(
                GateName,
                $"'{lineage}' already holds claim '{holder.ClaimId}'; "
                    + "record an update or a correction to supersede it");
    }

    // A retry re-sends the same claim, so the same identity is only excused when nothing about the claim
    // changed.
    private static bool IsTheSameClaim(FactRecord fact, RecordClaimCommand command) =>
        string.Equals(fact.ClaimId, command.ClaimId, StringComparison.Ordinal) &&
        string.Equals(fact.Body, command.Body, StringComparison.Ordinal) &&
        string.Equals(fact.Statement, command.Statement, StringComparison.Ordinal);
}
