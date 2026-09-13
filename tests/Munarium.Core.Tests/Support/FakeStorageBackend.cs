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
    private readonly Dictionary<string, List<LedgerEntry>> _streams = [];
    private int _conflictsRemaining;
    private long _globalSequence;

    /// <summary>Gets how many times <see cref="AppendAsync"/> was called.</summary>
    public int AppendCalls { get; private set; }

    /// <summary>Makes the next appends report a version conflict.</summary>
    /// <param name="times">How many appends to fail.</param>
    public void FailNextAppendsWithConflict(int times) => _conflictsRemaining = times;

    /// <inheritdoc />
    public ValueTask<SequenceNumber> HeadAsync(StreamId stream, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SequenceNumber(_streams.TryGetValue(stream.Value, out var entries) ? entries.Count : 0));

    /// <inheritdoc />
    public ValueTask<AppendOutcome> AppendAsync(
        StreamId stream,
        SequenceNumber expectedHead,
        IReadOnlyList<LedgerEvent> events,
        CancellationToken cancellationToken = default)
    {
        AppendCalls++;

        if (!_streams.TryGetValue(stream.Value, out var entries))
        {
            entries = [];
            _streams[stream.Value] = entries;
        }

        if (_conflictsRemaining > 0 || expectedHead.Value != entries.Count)
        {
            _conflictsRemaining = Math.Max(0, _conflictsRemaining - 1);
            return ValueTask.FromResult<AppendOutcome>(
                new VersionConflict(expectedHead, new SequenceNumber(entries.Count)));
        }

        foreach (var appended in events)
        {
            _globalSequence++;
            entries.Add(new LedgerEntry(
                new SequenceNumber(entries.Count + 1),
                new SequenceNumber(_globalSequence),
                appended));
        }

        return ValueTask.FromResult<AppendOutcome>(new Appended(new SequenceNumber(entries.Count)));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LedgerEntry>> ReadAsync(
        StreamId stream,
        SequenceNumber after,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<LedgerEntry>>(
            _streams.TryGetValue(stream.Value, out var entries)
                ? [.. entries.Skip((int)after.Value)]
                : []);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LedgerEntry>> ReadGlobalAsync(
        SequenceNumber upTo,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<LedgerEntry>>(
            [.. _streams.Values
                .SelectMany(entries => entries)
                .Where(entry => entry.GlobalSequence.Value <= upTo.Value)
                .OrderBy(entry => entry.GlobalSequence.Value)]);
}
