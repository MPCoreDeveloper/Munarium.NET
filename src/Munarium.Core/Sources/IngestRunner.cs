namespace Munarium.Sources;

using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Text;

/// <summary>
/// Ingests a document end to end: the bytes are stored, the row is recorded, the text is read, chunked, embedded and
/// written into the index version.
/// </summary>
/// <remarks>
/// The order is what makes a partial ingest impossible to mistake for a complete one. The media type is checked before
/// anything happens, so a document this port cannot read leaves no bytes and no row behind; the bytes are stored
/// before the row is recorded, so a row never describes bytes that are not there; and nothing is indexed before the
/// row exists, so every citation the index holds resolves to a path and a hash.
/// <para>
/// What this deliberately does not claim: an ingest that is refused for its media type, or a re-put of bytes that are
/// already there, does not put the document into the index. Whether a document is <em>in</em> a version is a catalogue
/// question - a version's manifest records the sources it indexed - and a deployment that lost its index rebuilds from
/// the rows rather than by re-uploading. Answering "unchanged" here means the row and the bytes were already there,
/// and nothing else.
/// </para>
/// </remarks>
/// <param name="ingest">The storage path: validate, hash, write, record.</param>
/// <param name="provider">The model seam, which the embeddings come from.</param>
/// <param name="host">The index instances, whose serving writer is where the chunks land.</param>
/// <param name="model">The embedding model to call.</param>
/// <param name="registry">Where the row's extraction outcome is recorded, which is what makes a scan visible.</param>
/// <param name="maxChunkChars">The largest a chunk may be, which is index identity material.</param>
public sealed class IngestRunner(
    SourceIngest ingest,
    IModelProvider provider,
    IIndexHost host,
    string model,
    ISourceRegistry registry,
    int maxChunkChars = IngestRunner.DefaultChunkChars)
{
    /// <summary>The chunk ceiling a deployment gets when it does not choose one.</summary>
    /// <remarks>
    /// A policy, not a truth: it travels into the index identity through <see cref="TextChunker.Version"/>, so a
    /// deployment that picks another size either matches this one or records a version of its own.
    /// </remarks>
    public const int DefaultChunkChars = 1200;

    private readonly SourceIngest _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
    private readonly IModelProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    private readonly IIndexHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly ISourceRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly string _model = string.IsNullOrWhiteSpace(model)
        ? throw new ArgumentException("The embedding model must be named.", nameof(model))
        : model;
    private readonly int _maxChunkChars = maxChunkChars > 0
        ? maxChunkChars
        : throw new ArgumentOutOfRangeException(nameof(maxChunkChars), maxChunkChars, "Must be positive.");

    /// <summary>
    /// Ingests a document.
    /// </summary>
    /// <param name="tenant">The tenant the path belongs to.</param>
    /// <param name="path">The logical path.</param>
    /// <param name="mediaType">The media type of the bytes, with or without parameters.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="declaredHash">The hash the caller declares, or <see langword="null"/> to hash what arrived.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document as indexed, the mismatch that stopped it, or the refusal that came first.</returns>
    /// <exception cref="ArgumentException">Thrown when the path is not an addressable document path.</exception>
    public async ValueTask<DocumentOutcome> IngestAsync(
        string tenant,
        string path,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        string? declaredHash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        if (!TextExtractor.CanExtract(mediaType))
        {
            return new IngestRefused(
                $"no extractor for media type '{mediaType}' is ported, so the document was not stored");
        }

        var outcome = await _ingest
            .PutAsync(tenant, path, mediaType, bytes, declaredHash, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is IngestRejected rejected)
        {
            return rejected;
        }

        var ingested = outcome is SourceIngested landed
            ? landed
            : throw new InvalidOperationException("The ingest answered with neither a row nor a rejection.");

        return ingested.Kind == SourceIngestKind.Unchanged
            ? new IngestedDocument(ingested.Record, ingested.Kind, 0, _host.ServingVersion)
            : await IndexAsync(ingested, mediaType, bytes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IngestedDocument> IndexAsync(
        SourceIngested ingested,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        // The serving writer is read once and then used: a cutover while this runs must not send half the chunks into
        // one version and the rest into another, and the answer names the writer's own version, so what it says is
        // where the chunks actually went.
        var writer = _host.ServingWriter;
        var extraction = TextExtractor.Extract(mediaType, bytes);

        // The row records how extraction went, here and in the build: the original calls this field the invisible-document
        // signal, and its point is that a deployment can read that a scan contributed nothing rather than see a document
        // that looks like it was never ingested.
        var recorded = await _registry
            .RecordExtractionAsync(
                ingested.Record.Tenant,
                ingested.Record.SourceId,
                extraction.Status.ToWireName(),
                extraction.Method.ToWireName(),
                cancellationToken)
            .ConfigureAwait(false) ?? ingested.Record;

        var chunks = TextChunker.Chunk(extraction.Text, _maxChunkChars);

        if (chunks.Count == 0)
        {
            // A document of nothing but whitespace is a stored source with nothing to retrieve. Recorded rather than
            // refused: the bytes are real, and a caller asking for that path deserves an answer.
            return new IngestedDocument(recorded, ingested.Kind, 0, writer.IndexVersion);
        }

        var response = await _provider
            .EmbedAsync(
                new EmbeddingRequest { Model = _model, Inputs = [.. chunks.Select(chunk => chunk.Text)] },
                cancellationToken)
            .ConfigureAwait(false);

        // One vector per chunk, in order, or nothing: a provider that answers with a different number would have its
        // vectors attached to the wrong text, which is a silent corruption rather than a visible failure.
        if (response.Vectors.Count != chunks.Count)
        {
            throw new InvalidOperationException(
                $"The provider returned {response.Vectors.Count} vectors for {chunks.Count} chunks; "
                    + "text and embedding cannot be paired, so nothing was indexed.");
        }

        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            writer.Index(
                Reference(ingested.Record, chunks[ordinal]),
                chunks[ordinal].Text,
                response.Vectors[ordinal].Span);
        }

        return new IngestedDocument(recorded, ingested.Kind, chunks.Count, writer.IndexVersion);
    }

    private static SourceReference Reference(SourceRecord record, TextChunk chunk) =>
        new(
            string.Concat(record.SourceId, "#", chunk.Ordinal),
            record.SourceId,
            record.Path,
            record.ContentHash,
            chunk.Ordinal);
}

/// <summary>
/// A document that was ingested, and how much of it reached the index.
/// </summary>
/// <remarks>
/// The chunk count is reported rather than assumed: a document that is stored but not indexed - because it held no
/// text, or because it was already there - is a real state, and a caller told "ingested" without a count would have no
/// way to notice it.
/// </remarks>
/// <param name="Record">The source row as recorded.</param>
/// <param name="Kind">Whether the path was new, replaced, or already held these bytes.</param>
/// <param name="ChunksIndexed">How many chunks were written into the index.</param>
/// <param name="IndexVersion">
/// The index version the chunks were written into, or the version that was serving when there was nothing to write.
/// </param>
public sealed record IngestedDocument(
    SourceRecord Record,
    SourceIngestKind Kind,
    int ChunksIndexed,
    string IndexVersion);

/// <summary>A document this port cannot read, so it was not stored.</summary>
/// <param name="Reason">Why it was refused.</param>
public sealed record IngestRefused(string Reason);

/// <summary>The outcome of an ingest: the document as indexed, the mismatch, or the refusal that came first.</summary>
public readonly union DocumentOutcome(IngestedDocument, IngestRejected, IngestRefused);
