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

    /// <summary>
    /// Reads a stream's events after <paramref name="after"/> (exclusive), in stream order.
    /// </summary>
    /// <param name="stream">The stream to read.</param>
    /// <param name="after">The sequence to read after; use <see cref="SequenceNumber.Zero"/> for all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entries in stream order, each carrying its stream and global position.</returns>
    ValueTask<IReadOnlyList<LedgerEntry>> ReadAsync(
        StreamId stream,
        SequenceNumber after,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the ledger across every stream in global order, up to and including
    /// <paramref name="upTo"/>.
    /// </summary>
    /// <param name="upTo">
    /// The <c>as_of</c> pin: the highest global position to include. Use
    /// <see cref="SequenceNumber.Zero"/> to read the empty ledger.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entries with a global position at or below the pin, in global order.</returns>
    ValueTask<IReadOnlyList<LedgerEntry>> ReadGlobalAsync(
        SequenceNumber upTo,
        CancellationToken cancellationToken = default);
}

