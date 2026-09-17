namespace Munarium.Sources;

/// <summary>
/// Where a source's document bytes live.
/// </summary>
/// <remarks>
/// Deliberately narrow: bytes in, bytes out, addressed by logical path. The metadata row is somebody else's
/// job, which is what lets a filesystem backend, a SharpCoreDB backend and an object-storage backend be
/// swapped without any of them knowing about the ledger or the index.
/// <para>
/// The implementations are the storage slice's, in the same way the ledger's <c>IStorageBackend</c> has one
/// adapter per store; the kernel only owns the seam and the rules a key has to satisfy.
/// </para>
/// </remarks>
public interface ISourceStore
{
    /// <summary>
    /// Writes bytes at the key's logical path, overwriting whatever was there.
    /// </summary>
    /// <param name="key">The source's address.</param>
    /// <param name="mediaType">The media type of the bytes, which the backend may record.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The backend-resolved URI, which the source row records.</returns>
    ValueTask<string> PutAsync(
        SourceKey key,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the bytes at the key's logical path.
    /// </summary>
    /// <param name="key">The source's address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bytes.</returns>
    ValueTask<byte[]> GetAsync(SourceKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether bytes exist at the key's logical path.
    /// </summary>
    /// <param name="key">The source's address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the blob exists.</returns>
    ValueTask<bool> ExistsAsync(SourceKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the blob at the key's logical path.
    /// </summary>
    /// <remarks>Idempotent: deleting an absent blob succeeds.</remarks>
    /// <param name="key">The source's address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask DeleteAsync(SourceKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the backend's identifier, which is recorded on the source row.
    /// </summary>
    /// <remarks>
    /// So an operator can tell where a document's bytes went without guessing.
    /// </remarks>
    string BackendId { get; }
}
