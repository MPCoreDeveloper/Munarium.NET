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
    private const int GlobalReadBatchSize = 512;

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

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LedgerEntry>> ReadAsync(
        StreamId stream,
        SequenceNumber after,
        CancellationToken cancellationToken = default)
    {
        var result = await _eventStore
            .ReadStreamAsync(
                new EventStreamId(stream.Value),
                new EventReadRange(after.Value + 1, long.MaxValue),
                cancellationToken)
            .ConfigureAwait(false);

        return Map(result.Events);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LedgerEntry>> ReadGlobalAsync(
        SequenceNumber upTo,
        CancellationToken cancellationToken = default)
    {
        if (upTo.Value <= 0)
        {
            return [];
        }

        var entries = new List<LedgerEntry>();
        var from = 1L;

        while (from <= upTo.Value)
        {
            var batch = await _eventStore
                .ReadAllAsync(from, GlobalReadBatchSize, cancellationToken)
                .ConfigureAwait(false);

            if (batch.Events.Count == 0)
            {
                break;
            }

            foreach (var envelope in batch.Events)
            {
                // The feed is in global order, so the first event past the pin ends the slice.
                if (envelope.GlobalSequence > upTo.Value)
                {
                    return entries;
                }

                entries.Add(Map(envelope));
            }

            from = batch.Events[^1].GlobalSequence + 1;
        }

        return entries;
    }

    private static LedgerEntry[] Map(IReadOnlyList<EventEnvelope> envelopes)
    {
        var entries = new LedgerEntry[envelopes.Count];

        for (var index = 0; index < envelopes.Count; index++)
        {
            entries[index] = Map(envelopes[index]);
        }

        return entries;
    }

    private static LedgerEntry Map(EventEnvelope envelope) => new(
        new SequenceNumber(envelope.Sequence),
        new SequenceNumber(envelope.GlobalSequence),
        new LedgerEvent(envelope.EventType, envelope.Payload));
}
