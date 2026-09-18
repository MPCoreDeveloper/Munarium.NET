namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;

/// <summary>
/// Tests for <see cref="SharpCoreDbIndexHost"/>: which version answers, and what a cutover does to the one that
/// stopped.
/// </summary>
public class SharpCoreDbIndexHostTests
{
    private const int Dimensions = 64;

    private static readonly DeterministicEmbeddingProvider Embedder = new(Dimensions);

    /// <summary>
    /// The writer and the reader of the serving version are the same index: a chunk written through the host is found
    /// by a search asked of the host, which is what makes the seam one seam rather than two that can drift.
    /// </summary>
    [Fact]
    public async Task TheInitialVersionServesAndWhatIsWrittenToItIsFound()
    {
        using var host = new SharpCoreDbIndexHost(Dimensions, "aot@1", SequenceNumber.Zero);

        Assert.Equal("aot@1", host.ServingVersion);

        // The engine reference is the one the retriever actually built with, which is why the host owns it.
        Assert.Equal(VectorIndexEngines.Of(VectorIndexKind.Exact), host.Engine);

        host.ServingWriter.Index(Chunk("chunk-1"), "the supplier is north", Embedder.Embed("the supplier is north"));

        var found = await host.ServingReader.SearchAsync(Query("supplier north"));

        Assert.Equal("chunk-1", Assert.Single(found.Chunks).Source.ChunkId);
        Assert.Equal("aot@1", found.Envelope.IndexVersion);
    }

    /// <summary>
    /// A build keeps an instance without answering from it: that is what lets a corpus be rebuilt while the version that
    /// is live keeps serving.
    /// </summary>
    [Fact]
    public async Task ABuiltVersionIsKeptButDoesNotAnswerUntilItIsServed()
    {
        using var host = new SharpCoreDbIndexHost(Dimensions, "aot@1", SequenceNumber.Zero);
        var built = host.Build("idx-new", new SequenceNumber(9));

        built.Writer.Index(Chunk("chunk-new"), "the bell rang twice", Embedder.Embed("the bell rang twice"));

        Assert.Equal("aot@1", host.ServingVersion);
        Assert.Empty((await host.ServingReader.SearchAsync(Query("the bell"))).Chunks);

        Assert.True(host.Serve("idx-new"));
        Assert.Equal("idx-new", host.ServingVersion);

        var found = await host.ServingReader.SearchAsync(Query("the bell"));

        Assert.Equal("chunk-new", Assert.Single(found.Chunks).Source.ChunkId);
        Assert.Equal(9, found.Envelope.LedgerWatermark.Value);
    }

    /// <summary>
    /// The version that stopped serving keeps answering: a question already handed to it must not lose its ground, and an
    /// envelope issued while it was live still has to be explainable.
    /// </summary>
    [Fact]
    public async Task TheVersionThatStoppedServingStillHoldsWhatItHeld()
    {
        using var host = new SharpCoreDbIndexHost(Dimensions, "aot@1", SequenceNumber.Zero);

        host.ServingWriter.Index(Chunk("chunk-old"), "the old bell", Embedder.Embed("the old bell"));

        var old = host.ServingReader;

        host.Build("idx-new", SequenceNumber.Zero);
        host.Serve("idx-new");

        var found = await old.SearchAsync(Query("the old bell"));

        Assert.Equal("chunk-old", Assert.Single(found.Chunks).Source.ChunkId);
        Assert.Equal("aot@1", found.Envelope.IndexVersion);
    }

    [Fact]
    public void CuttingOverToAVersionThatWasNeverBuiltChangesNothing()
    {
        using var host = new SharpCoreDbIndexHost(Dimensions, "aot@1", SequenceNumber.Zero);

        Assert.False(host.Serve("idx-never-built"));
        Assert.Equal("aot@1", host.ServingVersion);
    }

    /// <summary>Dropping what is serving would leave the deployment answering from nothing, so it is refused.</summary>
    [Fact]
    public void DiscardingWhatServesIsRefusedAndABuiltVersionIsDropped()
    {
        using var host = new SharpCoreDbIndexHost(Dimensions, "aot@1", SequenceNumber.Zero);
        host.Build("idx-doomed", SequenceNumber.Zero);

        Assert.False(host.Discard("aot@1"));
        Assert.Equal("aot@1", host.ServingVersion);

        Assert.True(host.Discard("idx-doomed"));
        Assert.False(host.Serve("idx-doomed"));
        Assert.Equal(["aot@1"], host.Versions);
    }

    [Fact]
    public void DisposingDropsEveryInstance()
    {
        var host = new SharpCoreDbIndexHost(Dimensions, "aot@1", SequenceNumber.Zero);
        host.Build("idx-second", SequenceNumber.Zero);

        Assert.Equal(2, host.Versions.Count);

        host.Dispose();

        Assert.Empty(host.Versions);
    }

    private static SourceReference Chunk(string chunkId) =>
        new(chunkId, "source-1", "docs/policy.txt", "sha256:policy", ChunkOrdinal: 0);

    private static RetrievalQuery Query(string text) =>
        new() { Text = text, Embedding = Embedder.Embed(text), TopK = 5 };
}
