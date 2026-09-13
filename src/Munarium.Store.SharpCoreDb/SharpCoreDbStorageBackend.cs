namespace Munarium.Store.SharpCoreDb;

using Munarium.Ledger;
using SharpCoreDB.EventSourcing;

/// <summary>
/// The Munarium ledger's storage seam, implemented over SharpCoreDB's event store.
/// </summary>
/// <remarks>
/// The kernel stays database-free: it only knows <see cref="IStorageBackend"/>. This class is the
/// one place that knows the SharpCoreDB wire types, and it maps the store's expected-version
/// append result onto the kernel's <see cref="AppendOutcome"/> union.
/// </remarks>
/// <param name="eventStore">The SharpCoreDB event store that holds the ledger streams.</param>
public sealed class SharpCoreDbStorageBackend(IEventStore eventStore) : IStorageBackend
{
    private readonly IEventStore _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));

    /// <inheritdoc />
    public async ValueTask<SequenceNumber> HeadAsync(StreamId stream, CancellationToken cancellationToken = default)
    {
        var length = await _eventStore
            .GetStreamLengthAsync(new EventStreamId(stream.Value), cancellationToken)
            .ConfigureAwait(false);

        return new SequenceNumber(length);
    }

    /// <inheritdoc />
    public async ValueTask<AppendOutcome> AppendAsync(
        StreamId stream,
        SequenceNumber expectedHead,
        IReadOnlyList<LedgerEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var entries = new EventAppendEntry[events.Count];
        var timestamp = DateTimeOffset.UtcNow;

        for (var index = 0; index < events.Count; index++)
        {
            entries[index] = new EventAppendEntry(
                events[index].Type,
                events[index].Payload,
                ReadOnlyMemory<byte>.Empty,
                timestamp);
        }

        var result = await _eventStore
            .TryAppendEventsAsync(new EventStreamId(stream.Value), expectedHead.Value, entries, cancellationToken)
            .ConfigureAwait(false);

        // Target-typed conditional expression: both branches convert to the AppendOutcome union.
        return result.Success
            ? new Appended(new SequenceNumber(result.ActualVersion))
            : new VersionConflict(
                new SequenceNumber(result.ExpectedVersion),
                new SequenceNumber(result.ActualVersion));
    }
}
