namespace Munarium.Retrieval;

using Munarium.Providers;
using Munarium.Sources;
using Munarium.Text;

/// <summary>
/// Builds an index version from the sources a deployment already has, and serves it only when asked to.
/// </summary>
/// <remarks>
/// The order is the design, and it is the opposite of the ingest's. A build reads every bound source, cuts it, embeds
/// it and writes it into an instance nobody is answering from - so the version that is live keeps answering while a
/// rebuild happens, which is the only way a corpus can be rebuilt without a window in which questions go unanswered.
/// The instance is then kept by the host, so activating the version is a cutover rather than a second build.
/// <para>
/// Nothing is skipped quietly. A bound document whose media type this port cannot read refuses the build and names the
/// path: an index that silently dropped it would claim to hold a collection it does not, and the one thing worse than an
/// incomplete index is an incomplete index nobody was told about. A document of nothing but whitespace, on the other
/// hand, is indexed as nothing - its bytes are real and its hash is in the manifest, so the version accounts for it.
/// </para>
/// <para>
/// The identity is derived before the build rather than after it, because the instance has to be named by the version it
/// will hold. That is the same derivation the catalogue performs and from the same material - the manifest and the bound
/// sources - so the row that is recorded is the version the chunks were written into, or the build was not a build of
/// what it says it was.
/// </para>
/// </remarks>
/// <param name="sources">Where the bytes are read from.</param>
/// <param name="registry">Where the rows that say which sources exist are.</param>
/// <param name="provider">The model seam the vectors come from.</param>
/// <param name="host">The index instances: one live, and the ones a build creates.</param>
/// <param name="catalogue">Where the version is recorded, and activated when the plan asks for it.</param>
/// <param name="embedder">The embedder a manifest records, which is identity material.</param>
/// <param name="maxChunkChars">The largest a chunk may be, which is identity material too.</param>
/// <param name="extraction">Local extraction first, and the document-intelligence provider only if it found nothing.</param>
/// <param name="chunkStore">Where a version chunks are kept, or <see langword="null"/> for none.</param>
public sealed class IndexBuilder(
    ISourceStore sources,
    ISourceRegistry registry,
    IModelProvider provider,
    IIndexHost host,
    IndexCatalog catalogue,
    EmbedderRef embedder,
    SourceExtraction? extraction = null,
    IIndexChunkStore? chunkStore = null,
    int maxChunkChars = IngestRunner.DefaultChunkChars)
{
    /// <summary>
    /// The extractors this port reads with, as a version.
    /// </summary>
    /// <remarks>
    /// Identity material: improving how a document becomes text changes the text for identical bytes, so it has to
    /// produce a new version rather than silently serving chunks that no longer match the bytes they cite.
    /// </remarks>
    public static string Extractors => TextExtractor.Version();

    private readonly ISourceStore _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    private readonly ISourceRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly SourceExtraction _extraction = extraction ?? new SourceExtraction();
    private readonly IIndexChunkStore? _chunks = chunkStore;
    private readonly IModelProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    private readonly IIndexHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly IndexCatalog _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly EmbedderRef _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    private readonly int _maxChunkChars = maxChunkChars > 0
        ? maxChunkChars
        : throw new ArgumentOutOfRangeException(nameof(maxChunkChars), maxChunkChars, "Must be positive.");

    /// <summary>Gets the versioned engine reference the host will build with.</summary>
    public string Engine => _host.Engine;

    /// <summary>
    /// Builds a version over the bound sources.
    /// </summary>
    /// <param name="plan">What to build, and over which sources.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version as recorded, or why nothing was built.</returns>
    public async ValueTask<IndexBuildOutcome> BuildAsync(
        IndexBuildPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var rows = await _registry
            .ListAsync(plan.Tenant, plan.PathPrefix, cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return new IndexBuildRefused(
                $"no sources are bound under '{plan.PathPrefix ?? "(every source)"}', so there is nothing to index");
        }

        var bound = rows
            .Select(row => new IndexedSource(row.SourceId, row.ContentHash))
            .ToList();

        var manifest = new IndexManifest
        {
            CollectionId = plan.CollectionId,
            CollectionName = plan.CollectionName,
            ShapeRef = plan.ShapeRef,
            Engine = _host.Engine,
            Chunker = TextChunker.Version,
            Extractors = Extractors,
            Embedder = _embedder,
            MaxChars = _maxChunkChars,
            SourceContentHashes = Hashes(rows),
        };

        var id = IndexVersionIds.Of(
            manifest.CollectionId,
            manifest.ShapeRef,
            manifest.Engine,
            manifest.Chunker,
            manifest.Extractors,
            manifest.Embedder,
            bound);

        var instance = _host.Build(id, plan.Watermark);
        var persisted = new List<PersistedChunk>();

        foreach (var row in rows)
        {
            var refusal = await IndexAsync(instance, plan.Tenant, row, persisted, cancellationToken).ConfigureAwait(false);

            // A refused build drops the instance rather than leaving it: a version that is half-built must not be
            // activatable by name, because it would answer with the documents the build got to before it stopped.
            if (refusal is not null)
            {
                _host.Discard(id);

                return refusal;
            }
        }

        // Written before the version is recorded, for the same reason the record is activated before it serves: a
        // version an operator can name has to be one this deployment can answer from, and rows without their chunks
        // would be a version that only looks built.
        if (_chunks is not null)
        {
            await _chunks.WriteAsync(id, persisted, cancellationToken).ConfigureAwait(false);
        }

        var recorded = await _catalogue
            .RegisterAsync(
                new IndexBuildRequest
                {
                    Tenant = plan.Tenant,
                    Manifest = manifest,
                    Sources = bound,
                    Watermark = plan.Watermark,
                    Activate = plan.Activate,
                    PathPrefix = plan.PathPrefix,
                },
                cancellationToken)
            .ConfigureAwait(false);

        // The record is activated before the process serves it, and that order matters: between the two an answer still
        // comes from the previous version and cites it, which resolves. The other order would produce answers citing a
        // version they did not come from.
        if (recorded.Active)
        {
            _host.Serve(recorded.Id);
        }

        return recorded;
    }

    /// <summary>Loads a version from its persisted chunks, without asking an embedder for anything.</summary>
    /// <remarks>
    /// The whole reason the chunks are persisted: a deployment that comes back holds the text a lexical index reads and
    /// the vectors a vector index reads, so recovery is a read and a write into a fresh instance rather than a second
    /// pass over the corpus. Nothing here is derived - these are the chunks the build indexed, in the order it did.
    /// </remarks>
    /// <param name="version">The version as the catalogue records it.</param>
    /// <param name="chunks">Its chunks, in the order the build wrote them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The instance, or <see langword="null"/> when there was nothing to load.</returns>
    public async ValueTask<IndexInstance?> LoadAsync(
        IndexVersion version,
        IReadOnlyList<PersistedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            return null;
        }

        var instance = _host.Build(version.Id, version.Watermark);

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            instance.Writer.Index(chunk.Source, chunk.Text, [.. chunk.Embedding]);
        }

        // Serving follows the record, exactly as a build does: an active version has to be the one answering.
        if (version.Active)
        {
            _host.Serve(version.Id);
        }

        return instance;
    }

    private async ValueTask<IndexBuildRefused?> IndexAsync(
        IndexInstance instance,
        string tenant,
        SourceRecord row,
        List<PersistedChunk> persisted,
        CancellationToken cancellationToken)
    {
        var bytes = await _sources
            .GetAsync(SourceKey.New(tenant, row.Path, row.ContentHash), cancellationToken)
            .ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            return new IndexBuildRefused(
                $"the row for '{row.Path}' says it holds bytes and the store returned none, so the corpus cannot be "
                    + "indexed as it is described");
        }

        if (!TextExtractor.CanExtract(row.MediaType))
        {
            return new IndexBuildRefused(
                $"'{row.Path}' is {row.MediaType}, which no extractor reads, so building would silently drop a "
                    + "document the collection binds");
        }

        var extracted = await _extraction.ExtractAsync(row.MediaType, bytes, cancellationToken).ConfigureAwait(false);

        // The row records how extraction went, which is the original's "invisible-document signal": a document that yields
        // nothing has to be visible in the data rather than look like one nobody ingested. The build is the original's
        // writer of these two fields; the ingest records the same outcome when it indexes a document as it arrives.
        await _registry
            .RecordExtractionAsync(
                tenant,
                row.SourceId,
                extracted.Status.ToWireName(),
                extracted.Method.ToWireName(),
                cancellationToken)
            .ConfigureAwait(false);

        if (extracted.Status is ExtractionStatus.Failed)
        {
            // The row records the failure, and the source contributes nothing rather than stopping the build. That
            // is the original's stance for an extraction that failed: its errors are data, and a corpus quietly
            // missing one document is visible in the rows, which is what these two fields exist for. A declaration
            // is different and still refuses a build: a media type no extractor reads cannot be recorded as
            // anything but a gap nobody can explain.
            return null;
        }

        var chunks = TextChunker.Chunk(extracted.Text, _maxChunkChars);

        if (chunks.Count == 0)
        {
            return null;
        }

        var response = await _provider
            .EmbedAsync(
                new EmbeddingRequest { Model = _embedder.Model, Inputs = [.. chunks.Select(chunk => chunk.Text)] },
                cancellationToken)
            .ConfigureAwait(false);

        if (response.Vectors.Count != chunks.Count)
        {
            throw new InvalidOperationException(
                $"The provider returned {response.Vectors.Count} vectors for {chunks.Count} chunks; "
                    + "text and embedding cannot be paired, so the version would be built from misaligned chunks.");
        }

        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            var reference = new SourceReference(
                string.Concat(row.SourceId, "#", chunks[ordinal].Ordinal),
                row.SourceId,
                row.Path,
                row.ContentHash,
                chunks[ordinal].Ordinal);

            instance.Writer.Index(reference, chunks[ordinal].Text, response.Vectors[ordinal].Span);

            // The same chunk, kept for the store: what a restart loads is what a build indexed, and the embedding
            // travels with it rather than being recomputed - which is the difference between a read and another pass
            // over the corpus.
            persisted.Add(new PersistedChunk
            {
                Source = reference,
                Text = chunks[ordinal].Text,
                Embedding = [.. response.Vectors[ordinal].Span],
            });
        }

        return null;
    }

    // Sorted and deduped, so the same corpus reads as the same manifest however the rows happened to be ordered - which
    // matters because the manifest is what the identity is derived from.
    private static List<string> Hashes(IReadOnlyList<SourceRecord> rows) =>
        [.. rows.Select(row => row.ContentHash).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
