namespace Munarium.Ledger;

/// <summary>
/// The storage seam of the Munarium kernel: an append-only, optimistic-concurrency
/// fact ledger. Concrete backends (SharpCoreDB, PostgreSQL, in-memory) live behind
/// this interface, so the kernel never depends on a specific database.
/// </summary>
/// <remarks>
/// Implementations must make the conditional append atomic: a writer that supplies a
/// stale expected head is told so and nothing is written. This is the property that
/// turns "append-only" into a governed ledger.
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
}
