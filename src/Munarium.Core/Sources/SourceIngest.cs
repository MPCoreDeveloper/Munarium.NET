namespace Munarium.Sources;

using Munarium.Evidence;

/// <summary>
/// The ingest path: validate the path, hash the bytes, write them, record the row.
/// </summary>
/// <remarks>
/// The order is the design. The path is refused before anything is looked up or written, so a traversal or the
/// reserved evidence keyspace cannot reach the store at all; the hash is computed from the bytes that arrived, so the
/// row cannot describe bytes nobody wrote; a declared hash is verified before the write, so a mismatch costs nothing
/// to reject; and an ingest that changes nothing writes nothing, because a command that changed nothing must not look
/// like one that did.
/// <para>
/// A mismatch is an outcome rather than an exception: it is not a malformed request, it is a document that is not the
/// document the caller said it was - and the caller has to be told which hash arrived, or it cannot tell a truncated
/// upload from a wrong one.
/// </para>
/// </remarks>
/// <param name="store">Where the bytes go.</param>
/// <param name="registry">Where the rows go.</param>
public sealed class SourceIngest(ISourceStore store, ISourceRegistry registry)
{
    private readonly ISourceStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ISourceRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>
    /// Ingests a document.
    /// </summary>
    /// <param name="tenant">The tenant the path belongs to.</param>
    /// <param name="path">The logical path.</param>
    /// <param name="mediaType">The media type of the bytes.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="declaredHash">The hash the caller declares, or <see langword="null"/> to hash what arrived.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row as recorded, or the mismatch that stopped it.</returns>
    /// <exception cref="ArgumentException">Thrown when the path is not an addressable document path.</exception>
    public async ValueTask<IngestOutcome> PutAsync(
        string tenant,
        string path,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        string? declaredHash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        // Refused before anything else: the reserved keyspace holds sealed evidence artifacts, and a document written
        // there could collide with one.
        SourcePaths.RefuseReservedDocumentPath(path);

        var hash = ArtifactContent.Hash(bytes.Span);

        if (declaredHash is { Length: > 0 } declared && !string.Equals(declared, hash, StringComparison.Ordinal))
        {
            return new IngestRejected(declared, hash);
        }

        // Constructing the key validates the path, so nothing below this line can be reached by a path the store is
        // not allowed to hold.
        var known = await _registry.FindAsync(tenant, path, cancellationToken).ConfigureAwait(false);

        if (known is not null && string.Equals(known.ContentHash, hash, StringComparison.Ordinal))
        {
            return new SourceIngested(known, SourceIngestKind.Unchanged);
        }

        var key = SourceKey.New(tenant, path, hash);
        var uri = await _store.PutAsync(key, mediaType, bytes, cancellationToken).ConfigureAwait(false);

        var recorded = await _registry
            .RecordAsync(
                new SourceRecord
                {
                    Tenant = tenant,
                    SourceId = key.SourceId,
                    Path = path,
                    ContentHash = hash,
                    MediaType = mediaType,
                    BytesLength = bytes.Length,
                    BlobUri = uri,
                    BackendId = _store.BackendId,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new SourceIngested(
            recorded,
            known is null ? SourceIngestKind.New : SourceIngestKind.Replaced);
    }
}

/// <summary>What an ingest did to a path.</summary>
public enum SourceIngestKind
{
    /// <summary>The path was not a source before.</summary>
    New = 0,

    /// <summary>The path was a source and now holds different bytes.</summary>
    Replaced = 1,

    /// <summary>The path already held exactly these bytes, so nothing was written.</summary>
    Unchanged = 2,
}

/// <summary>A document that was ingested, and what that did to its path.</summary>
/// <param name="Record">The row as recorded.</param>
/// <param name="Kind">Whether the path was new, replaced, or already held these bytes.</param>
public sealed record SourceIngested(SourceRecord Record, SourceIngestKind Kind);

/// <summary>A document that is not the document the caller declared.</summary>
/// <param name="Declared">The hash the caller declared.</param>
/// <param name="Actual">The hash of the bytes that arrived.</param>
public sealed record IngestRejected(string Declared, string Actual);

/// <summary>The outcome of an ingest: the row as recorded, or the mismatch that stopped it.</summary>
public readonly union IngestOutcome(SourceIngested, IngestRejected);
