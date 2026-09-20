namespace Munarium.Core.Tests.Support;

using Munarium.Retrieval;

/// <summary>An index-chunk store held in a list, for tests that need one without a database.</summary>
/// <remarks>
/// It keeps the rule the real one does - a version's chunks are appended, read in the order they were written, and
/// forgotten together - so a test that passes here is a statement about the seam and not about this list.
/// </remarks>
public sealed class InMemoryIndexChunkStore : IIndexChunkStore
{
    private readonly Dictionary<string, List<PersistedChunk>> _versions = new(StringComparer.Ordinal);

    /// <summary>Gets how many times a version was written to.</summary>
    public int Writes { get; private set; }

    /// <inheritdoc />
    public ValueTask<int> WriteAsync(
        string indexVersion,
        IReadOnlyList<PersistedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(chunks);

        if (!_versions.TryGetValue(indexVersion, out var stored))
        {
            stored = [];
            _versions[indexVersion] = stored;
        }

        stored.AddRange(chunks);
        Writes++;

        return ValueTask.FromResult(chunks.Count);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PersistedChunk>> ReadAsync(
        string indexVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<PersistedChunk>>(
            _versions.TryGetValue(indexVersion, out var stored) ? [.. stored] : []);
    }

    /// <inheritdoc />
    public ValueTask<bool> DropAsync(string indexVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_versions.Remove(indexVersion));
    }
}
