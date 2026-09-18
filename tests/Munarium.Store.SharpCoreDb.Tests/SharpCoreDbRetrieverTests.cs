namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;

/// <summary>
/// Tests for the retrieval backend: SharpCoreDB's vector index plus a lexical leg, fused by RRF,
/// with the provenance envelope sealed over whatever the answer used.
/// </summary>
public class SharpCoreDbRetrieverTests
{
    [Fact]
    public async Task TheClosestChunkWinsAndTheEnvelopeNamesItsSource()
    {
        var embedder = new DeterministicEmbeddingProvider(256);
        using var retriever = new SharpCoreDbRetriever(256, "index@1", new SequenceNumber(12));

        Index(retriever, embedder, "chunk-a", "docs/sanctions.pdf", "sanctioned supplier policy");
        Index(retriever, embedder, "chunk-b", "docs/weather.pdf", "weather over the north sea");

        var result = await retriever.SearchAsync(new RetrievalQuery
        {
            Text = "sanctioned supplier policy",
            Embedding = embedder.Embed("sanctioned supplier policy"),
            TopK = 2,
        });

        Assert.Equal("chunk-a", result.Chunks[0].Source.ChunkId);
        Assert.Equal("index@1", result.Envelope.IndexVersion);
        Assert.Equal(new SequenceNumber(12), result.Envelope.LedgerWatermark);
        Assert.Equal(result.Chunks.Select(chunk => chunk.Source), result.Envelope.Sources);
        Assert.Equal("docs/sanctions.pdf", result.Envelope.Sources[0].SourcePath);
    }

    [Fact]
    public async Task TheLexicalLegAloneStillAnswers()
    {
        var embedder = new DeterministicEmbeddingProvider(64);
        using var retriever = new SharpCoreDbRetriever(64, "index@2", SequenceNumber.Zero);

        Index(retriever, embedder, "chunk-a", "docs/sanctions.pdf", "sanctioned supplier policy");
        Index(retriever, embedder, "chunk-b", "docs/weather.pdf", "weather over the north sea");

        var result = await retriever.SearchAsync(new RetrievalQuery
        {
            Text = "weather north sea",
            TopK = 1,
        });

        Assert.Equal("chunk-b", Assert.Single(result.Chunks).Source.ChunkId);
    }

    /// <summary>
    /// BM25's whole point: a term the corpus rarely uses carries more information than one it uses everywhere, so a
    /// chunk holding the rare term outranks chunks holding only the common one.
    /// </summary>
    [Fact]
    public async Task ARareTermOutranksACommonOne()
    {
        var embedder = new DeterministicEmbeddingProvider(32);
        using var retriever = new SharpCoreDbRetriever(32, "index@4", SequenceNumber.Zero);

        Index(retriever, embedder, "chunk-a", "docs/sanctions.pdf", "sanctioned supplier policy");
        Index(retriever, embedder, "chunk-b", "docs/review.pdf", "supplier policy review");
        Index(retriever, embedder, "chunk-c", "docs/audit.pdf", "supplier policy audit");

        var result = await retriever.SearchAsync(new RetrievalQuery { Text = "sanctioned policy", TopK = 3 });

        Assert.Equal("chunk-a", result.Chunks[0].Source.ChunkId);
    }

    /// <summary>
    /// Positions are stored so a phrase can be preferred to the same terms scattered, which is exactly what a
    /// term-overlap ranker could not see.
    /// </summary>
    [Fact]
    public async Task APhraseOutranksTheSameTermsScattered()
    {
        var embedder = new DeterministicEmbeddingProvider(32);
        using var retriever = new SharpCoreDbRetriever(32, "index@5", SequenceNumber.Zero);

        Index(retriever, embedder, "chunk-adjacent", "docs/a.pdf", "the sanctioned supplier policy applies");
        Index(retriever, embedder, "chunk-scattered", "docs/b.pdf", "the supplier review policy sanctioned it");

        var result = await retriever.SearchAsync(new RetrievalQuery { Text = "\"sanctioned supplier\"", TopK = 2 });

        Assert.Equal("chunk-adjacent", result.Chunks[0].Source.ChunkId);
        Assert.Equal(2, result.Chunks.Count);
    }

    /// <summary>
    /// The engine is an index-version decision: a version built by the graph answers the same question, and records
    /// a different engine - which is why the engine is identity material rather than a query-time preference.
    /// </summary>
    [Fact]
    public async Task ADiskAnnVersionAnswersTheSameQuestionAndRecordsItsEngine()
    {
        var embedder = new DeterministicEmbeddingProvider(64);
        using var exact = new SharpCoreDbRetriever(64, "index@6", new SequenceNumber(3));
        using var approximate = new SharpCoreDbRetriever(64, "index@7", new SequenceNumber(3), VectorIndexKind.DiskAnn);

        foreach (var (chunkId, path, text) in Corpus())
        {
            Index(exact, embedder, chunkId, path, text);
            Index(approximate, embedder, chunkId, path, text);
        }

        var query = new RetrievalQuery
        {
            Text = "sanctions screening",
            Embedding = embedder.Embed("sanctions screening"),
            TopK = 2,
        };

        var exactAnswer = await exact.SearchAsync(query);
        var approximateAnswer = await approximate.SearchAsync(query);

        Assert.Equal("chunk-sanctions", exactAnswer.Chunks[0].Source.ChunkId);
        Assert.Equal("chunk-sanctions", approximateAnswer.Chunks[0].Source.ChunkId);
        Assert.Equal("index@7", approximateAnswer.Envelope.IndexVersion);
        Assert.Equal(approximateAnswer.Chunks.Select(chunk => chunk.Source), approximateAnswer.Envelope.Sources);
    }

    [Fact]
    public void TheEngineReferenceIsVersionedAndDistinct()
    {
        using var exact = new SharpCoreDbRetriever(16, "index@8", SequenceNumber.Zero);
        using var approximate = new SharpCoreDbRetriever(16, "index@9", SequenceNumber.Zero, VectorIndexKind.DiskAnn);

        Assert.Equal(VectorIndexEngines.Exact, exact.Engine);
        Assert.StartsWith("diskann@", approximate.Engine, StringComparison.Ordinal);
        Assert.NotEqual(exact.Engine, approximate.Engine);
        Assert.Equal(VectorIndexKind.DiskAnn, approximate.Kind);
    }

    [Fact]
    public async Task AnEmbeddingOfTheWrongWidthIsRefused()
    {
        using var retriever = new SharpCoreDbRetriever(16, "index@3", SequenceNumber.Zero);

        var exception = await Record.ExceptionAsync(async () => await retriever.SearchAsync(new RetrievalQuery
        {
            Text = "anything",
            Embedding = new float[8],
            TopK = 1,
        }));

        Assert.IsType<ArgumentException>(exception);
    }

    private static IReadOnlyList<(string ChunkId, string Path, string Text)> Corpus() =>
    [
        ("chunk-sanctions", "docs/sanctions.pdf", "sanctions screening list policy"),
        ("chunk-weather", "docs/weather.pdf", "weather over the north sea"),
        ("chunk-invoices", "docs/invoices.pdf", "invoice approval thresholds"),
        ("chunk-dpa", "docs/dpa.pdf", "data processing agreement terms"),
    ];

    private static void Index(
        SharpCoreDbRetriever retriever,
        DeterministicEmbeddingProvider embedder,
        string chunkId,
        string sourcePath,
        string text) =>
        retriever.Index(
            new SourceReference(chunkId, "source-1", sourcePath, "sha256:" + chunkId, ChunkOrdinal: 0),
            text,
            embedder.Embed(text));
}
