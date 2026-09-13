namespace Munarium.Core.Tests.Support;

using Munarium.Ledger;

/// <summary>
/// An in-memory <see cref="IStorageBackend"/> for kernel tests.
/// </summary>
/// <remarks>
/// It can be told to report a version conflict a number of times, which is how the write path's
/// retry loop is tested deterministically without needing a second real writer.
/// </remarks>
internal sealed class FakeStorageBackend : IStorageBackend
{
    private readonly Dictionary<string, List<LedgerEvent>> _streams = [];
    private int _conflictsRemaining;

    /// <summary>Gets how many times <see cref="AppendAsync"/> was called.</summary>
    public int AppendCalls { get; private set; }

    /// <summary>Makes the next appends report a version conflict.</summary>
    /// <param name="times">How many appends to fail.</param>
    public void FailNextAppendsWithConflict(int times) => _conflictsRemaining = times;

    /// <inheritdoc />
    public ValueTask<SequenceNumber> HeadAsync(StreamId stream, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SequenceNumber(_streams.TryGetValue(stream.Value, out var events) ? events.Count : 0));

    /// <inheritdoc />
    public ValueTask<AppendOutcome> AppendAsync(
        StreamId stream,
        SequenceNumber expectedHead,
        IReadOnlyList<LedgerEvent> events,
        CancellationToken cancellationToken = default)
    {
        AppendCalls++;

        if (!_streams.TryGetValue(stream.Value, out var streamEvents))
        {
            streamEvents = [];
            _streams[stream.Value] = streamEvents;
        }

        if (_conflictsRemaining > 0 || expectedHead.Value != streamEvents.Count)
        {
            _conflictsRemaining = Math.Max(0, _conflictsRemaining - 1);
            return ValueTask.FromResult<AppendOutcome>(
                new VersionConflict(expectedHead, new SequenceNumber(streamEvents.Count)));
        }

        streamEvents.AddRange(events);
        return ValueTask.FromResult<AppendOutcome>(new Appended(new SequenceNumber(streamEvents.Count)));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LedgerEvent>> ReadAsync(
        StreamId stream,
        SequenceNumber after,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<LedgerEvent>>(
            _streams.TryGetValue(stream.Value, out var events)
                ? [.. events.Skip((int)after.Value)]
                : []);
}
