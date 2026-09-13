namespace Munarium.Ledger;

/// <summary>
/// The storage seam of the Munarium kernel: an append-only, optimistic-concurrency fact ledger.
/// Concrete backends (SharpCoreDB, PostgreSQL, in-memory) live behind this interface, so the
/// kernel never depends on a specific database.
/// </summary>
/// <remarks>
/// Implementations must make the conditional append atomic: a writer that supplies a stale
/// expected head is told so and nothing is written. This is the property that turns
/// "append-only" into a governed ledger.
/// </remarks>
public interface IStorageBackend
{
    /// <summary>
    /// Reads the current head of a stream - the highest assigned sequence - or
    /// <see cref="SequenceNumber.Zero"/> when the stream holds no events.
    /// </summary>
    /// <param name="stream">The stream to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current stream head.</returns>
    ValueTask<SequenceNumber> HeadAsync(StreamId stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends events only when the stream head still equals <paramref name="expectedHead"/>,
    /// as one atomic compare-and-set.
    /// </summary>
    /// <param name="stream">The stream to append to.</param>
    /// <param name="expectedHead">The head the caller last observed.</param>
    /// <param name="events">The events to append, in order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Appended"/> carrying the new head, or <see cref="VersionConflict"/> when
    /// another writer moved the head first - in which case nothing was written.
    /// </returns>
    ValueTask<AppendOutcome> AppendAsync(
        StreamId stream,
        SequenceNumber expectedHead,
        IReadOnlyList<LedgerEvent> events,
        CancellationToken cancellationToken = default);
}

