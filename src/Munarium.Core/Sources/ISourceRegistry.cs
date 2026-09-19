namespace Munarium.Sources;

/// <summary>
/// Where the source metadata rows live, one per path.
/// </summary>
/// <remarks>
/// Separate from <see cref="ISourceStore"/>, which moves bytes: a row is read to decide what to index and to answer
/// where a chunk came from, and the two change at different rates - bytes are written once and read often, a row is
/// upserted on every ingest.
/// <para>
/// The implementations are the ingest slice's; the kernel owns the seam and the rules an ingest has to satisfy.
/// </para>
/// </remarks>
public interface ISourceRegistry
{
    /// <summary>
    /// Records a row, replacing the one for the same tenant and path.
    /// </summary>
    /// <remarks>
    /// A path is a source's identity, so this is an upsert rather than an append: re-ingesting a path is a new
    /// version of one source, not a second source.
    /// </remarks>
    /// <param name="record">The row to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row as recorded.</returns>
    ValueTask<SourceRecord> RecordAsync(SourceRecord record, CancellationToken cancellationToken = default);

    /// <summary>Reads the row for a path.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="path">The logical path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or <see langword="null"/> when that path was never ingested.</returns>
    ValueTask<SourceRecord?> FindAsync(
        string tenant,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the row for a source identity.
    /// </summary>
    /// <remarks>
    /// A second lookup rather than a derived one: a source id is a hash of tenant and path, so it cannot be turned back
    /// into a path - which is deliberate, because an id that revealed its path would leak the path to anyone holding an
    /// id, and a citation carries ids.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="sourceId">The source identity, as <c>src-…</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or <see langword="null"/> when no source with that identity was ingested.</returns>
    ValueTask<SourceRecord?> GetAsync(
        string tenant,
        string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a tenant's rows, optionally only those under a path prefix.
    /// </summary>
    /// <remarks>
    /// The prefix is how a collection binds its sources: a collection is a set of documents, and the set is the rows
    /// under a prefix. There is no separate membership table, so there is nothing that can disagree with the rows.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="pathPrefix">The path prefix to select, or <see langword="null"/> for every source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows, in path order.</returns>
    ValueTask<IReadOnlyList<SourceRecord>> ListAsync(
        string tenant,
        string? pathPrefix = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records how a source's extraction went, leaving the rest of the row alone.
    /// </summary>
    /// <remarks>
    /// The original writes these two fields with a single UPDATE at index time, and it is a separate operation from an
    /// ingest for a reason: the row may have been written days earlier, so rewriting it from a stale copy would undo an
    /// ingest that happened in between. Passing <see langword="null"/> for both clears them, which is what a re-upload
    /// owes - the new bytes have not been read yet.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="sourceId">The source identity, as <c>src-…</c>.</param>
    /// <param name="status">The status name, or <see langword="null"/> to clear it.</param>
    /// <param name="method">The method name, or <see langword="null"/> to clear it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row as it stands, or <see langword="null"/> when no source has that identity.</returns>
    ValueTask<SourceRecord?> RecordExtractionAsync(
        string tenant,
        string sourceId,
        string? status,
        string? method,
        CancellationToken cancellationToken = default);
}
