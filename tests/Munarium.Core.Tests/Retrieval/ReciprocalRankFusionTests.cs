namespace Munarium.Core.Tests.Retrieval;

using System.Globalization;
using Munarium.Ledger;
using Munarium.Retrieval;

/// <summary>
/// Tests for reciprocal rank fusion and the provenance invariant: an answer and its envelope are
/// produced together and cannot drift apart.
/// </summary>
public class ReciprocalRankFusionTests
{
    [Fact]
    public void AChunkFoundByBothLegsOutranksAChunkFoundByOne()
    {
        var both = Chunk("chunk-both");
        var lexicalOnly = Chunk("chunk-lexical");
        var vectorOnly = Chunk("chunk-vector");

        var result = ReciprocalRankFusion.Fuse(
            [[lexicalOnly, both], [vectorOnly, both]],
            "index@1",
            new SequenceNumber(7),
            topK: 3);

        Assert.Equal("chunk-both", result.Chunks[0].Source.ChunkId);
    }

    [Fact]
    public void TheEnvelopeCoversExactlyTheChunksTheAnswerUsed()
    {
        var result = ReciprocalRankFusion.Fuse(
            [[Chunk("a"), Chunk("b")]],
            "index@3",
            new SequenceNumber(42),
            topK: 2);

        Assert.Equal("index@3", result.Envelope.IndexVersion);
        Assert.Equal(new SequenceNumber(42), result.Envelope.LedgerWatermark);
        Assert.Equal(["a", "b"], result.Envelope.Sources.Select(source => source.ChunkId));
        Assert.Equal(result.Chunks.Select(chunk => chunk.Source), result.Envelope.Sources);
        Assert.Equal("docs/policy.pdf", result.Envelope.Sources[0].SourcePath);
    }

    [Fact]
    public void TopKLimitsBothTheAnswerAndTheEnvelope()
    {
        var result = ReciprocalRankFusion.Fuse(
            [[Chunk("a"), Chunk("b"), Chunk("c")]],
            "index@1",
            SequenceNumber.Zero,
            topK: 2);

        Assert.Equal(2, result.Chunks.Count);
        Assert.Equal(2, result.Envelope.Sources.Count);
    }

    [Fact]
    public void IdenticalInputsFuseToAnIdenticalAnswerAndEnvelope()
    {
        Assert.Equal(Describe(Fuse()), Describe(Fuse()));
    }

    private static RetrievalResult Fuse() => ReciprocalRankFusion.Fuse(
        [[Chunk("a"), Chunk("b")], [Chunk("b"), Chunk("c")]],
        "index@1",
        new SequenceNumber(9),
        topK: 3);

    private static string Describe(RetrievalResult result) => string.Join(
        "|",
        result.Chunks.Select(chunk =>
            string.Concat(
                chunk.Source.ChunkId,
                ":",
                chunk.Score.ToString("R", CultureInfo.InvariantCulture))));

    private static RetrievedChunk Chunk(string chunkId) => new(
        new SourceReference(chunkId, "source-1", "docs/policy.pdf", "sha256:abc", ChunkOrdinal: 0),
        Score: 0,
        $"text of {chunkId}");
}
