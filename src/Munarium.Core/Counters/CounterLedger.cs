namespace Munarium.Counters;

using Munarium.Claims;
using Munarium.Ledger;

/// <summary>
/// The write path for counters: a whole-document total is recorded, and the later one wins.
/// </summary>
/// <remarks>
/// The total is absolute rather than a delta, which is why this write path is an upsert: a reader must not have to
/// sum a stream to know how often a pattern was used, because a budget checked against a sum changes meaning when an
/// event is lost. The consequence is worth stating rather than hiding: a writer that records a <em>lower</em> total
/// is correcting the count, and this plane cannot tell a correction from an evasion - a counter never sees the text
/// it counts, so only the writer can be wrong about it.
/// <para>
/// Counting and judging stay separate: <see cref="CounterBudget.Count"/> computes a total and
/// <see cref="CounterBudget.Findings"/> judges one, so a counter can be kept before anybody sets a ceiling - and the
/// total is already right when somebody does.
/// </para>
/// </remarks>
/// <param name="storage">The ledger's storage seam.</param>
/// <param name="maxAttempts">How many times a contended write is retried.</param>
public sealed class CounterLedger(IStorageBackend storage, int maxAttempts = 3)
{
    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly int _maxAttempts = maxAttempts > 0
        ? maxAttempts
        : throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be positive.");

    /// <summary>
    /// Records a whole-document total for a counter key.
    /// </summary>
    /// <param name="versionId">The version the count belongs to.</param>
    /// <param name="key">The pattern the counter counts.</param>
    /// <param name="total">The total as it stands, which is absolute rather than a delta.</param>
    /// <param name="budget">The ceiling the writer was working under, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total as recorded, or the race that stopped it.</returns>
    /// <exception cref="ArgumentException">Thrown when the key or the version is missing.</exception>
    public async ValueTask<CounterOutcome> RecordAsync(
        string versionId,
        string key,
        ulong total,
        ulong? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var stream = StreamId.From(versionId);
        var lastExpected = SequenceNumber.Zero;
        var lastActual = SequenceNumber.Zero;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var head = await _storage.HeadAsync(stream, cancellationToken).ConfigureAwait(false);
            lastExpected = head;

            var counter = new CounterTotal { Key = key, Total = total, Budget = budget };

            var result = await _storage
                .AppendAsync(
                    stream,
                    head,
                    [new LedgerEvent(CounterCodec.RecordedEventType, CounterCodec.Encode(counter))],
                    cancellationToken)
                .ConfigureAwait(false);

            if (result is Appended)
            {
                // A counter carries no position of its own: it is a total about the whole document, and the plane
                // keys it by the pattern rather than by the write that last moved it.
                return counter;
            }

            if (result is VersionConflict conflict)
            {
                lastExpected = conflict.Expected;
                lastActual = conflict.Actual;
            }
        }

        return new WriteContended(lastExpected, lastActual);
    }
}
