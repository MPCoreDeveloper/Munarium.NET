namespace Munarium.Core.Tests.Support;

using Munarium.Sources;

/// <summary>
/// An in-memory <see cref="ISourceStore"/> for kernel tests.
/// </summary>
/// <remarks>
/// It keeps bytes under the blob name a key composes, which is what makes it a useful double rather than a
/// dictionary of keys: a test can prove that two tenants with the same path do not see each other's bytes,
/// and that a path an attacker supplied cannot reach outside the store it was given.
/// </remarks>
internal sealed class InMemorySourceStore : ISourceStore
{
    // Blob names are string keys, and a Dictionary compares those ordinally by default - which is the
    // comparison a path needs.
    private readonly Dictionary<string, byte[]> _blobs = [];
    private readonly Dictionary<string, string> _mediaTypes = [];

    /// <summary>Gets how many blobs are held.</summary>
    public int Count => _blobs.Count;

    /// <inheritdoc />
    public string BackendId => "mem";

    /// <summary>Gets the media type recorded for a blob name, when one was written.</summary>
    /// <param name="blobName">The blob name.</param>
    /// <returns>The media type, or <see langword="null"/>.</returns>
    public string? MediaTypeOf(string blobName) => _mediaTypes.GetValueOrDefault(blobName);

    /// <inheritdoc />
    public ValueTask<string> PutAsync(
        SourceKey key,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _blobs[key.BlobName] = bytes.ToArray();
        _mediaTypes[key.BlobName] = mediaType;

        return ValueTask.FromResult($"mem://{key.BlobName}");
    }

    /// <inheritdoc />
    public ValueTask<byte[]> GetAsync(SourceKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return ValueTask.FromResult(
            _blobs.TryGetValue(key.BlobName, out var bytes) ? bytes : []);
    }

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(SourceKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return ValueTask.FromResult(_blobs.ContainsKey(key.BlobName));
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(SourceKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _blobs.Remove(key.BlobName);
        _mediaTypes.Remove(key.BlobName);

        return ValueTask.CompletedTask;
    }
}
