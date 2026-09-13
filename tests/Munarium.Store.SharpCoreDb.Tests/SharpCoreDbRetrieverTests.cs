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

    [Fact]
    public void AnEmbeddingOfTheWrongWidthIsRefused()
    {
        using var retriever = new SharpCoreDbRetriever(16, "index@3", SequenceNumber.Zero);

        var exception = Record.Exception(() => retriever.SearchAsync(new RetrievalQuery
        {
            Text = "anything",
            Embedding = new float[8],
            TopK = 1,
        }));

        Assert.IsType<ArgumentException>(exception);
    }

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
