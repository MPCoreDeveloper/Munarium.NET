namespace Munarium.Core.Tests.Retrieval;

using Munarium.Retrieval;

/// <summary>
/// Tests for the index version's identity: it is derived from everything that determines what a query matches, so
/// a rebuild is idempotent and any real change is a new version.
/// </summary>
public class IndexVersionTests
{
    [Fact]
    public void TheIdentityIsDerivedAndStable()
    {
        var id = Id();

        Assert.StartsWith("idx-", id, StringComparison.Ordinal);
        Assert.Equal(20, id.Length);
        Assert.Equal(id, Id());
    }

    /// <summary>
    /// A caller's ordering is not identity material: the same corpus bound in a different order is the same corpus,
    /// and an identity that flipped with insertion order would rebuild an index nobody changed.
    /// </summary>
    [Fact]
    public void TheOrderSourcesAreBoundInIsNotIdentityMaterial() =>
        Assert.Equal(Id(sources: [Source("src-1", "sha256:aa"), Source("src-2", "sha256:bb")]), Id(sources: [Source("src-2", "sha256:bb"), Source("src-1", "sha256:aa")]));

    /// <summary>
    /// The bug a unit separator closes, the same one the evidence plane's domain key learned: material joined with
    /// a character that can also appear in the material can be reached two ways, and two different corpora would
    /// then share one identity.
    /// </summary>
    [Fact]
    public void OneSourceIdHoldingTheSeparatorIsNotTwoSources() =>
        Assert.NotEqual(Id(sources: [Source("src-1\u001fsha256:aa", "sha256:aa")]), Id(sources: [Source("src-1", "sha256:aa")]));

    /// <summary>Two sources that happen to share bytes are still two sources, and one path re-put with new bytes is another corpus.</summary>
    [Fact]
    public void IdentityPairsTheSourceWithItsBytes()
    {
        Assert.NotEqual(Id(sources: [Source("src-1", "sha256:aa")]), Id(sources: [Source("src-1", "sha256:aa"), Source("src-2", "sha256:aa")]));
        Assert.NotEqual(Id(sources: [Source("src-1", "sha256:aa")]), Id(sources: [Source("src-1", "sha256:bb")]));
    }

    /// <summary>
    /// Every change that alters the text or the vectors has to mint a new version - including the extractor, which
    /// changes the text for identical bytes; the embedder, which changes what a query matches; and the engine,
    /// because an approximate index and an exact one over one corpus are two different indexes.
    /// </summary>
    [Fact]
    public void EveryChangeThatAltersTheTextOrTheVectorsMintsANewVersion()
    {
        var baseline = Id();

        Assert.NotEqual(baseline, Id(collection: "other"));
        Assert.NotEqual(baseline, Id(shape: "document@2"));
        Assert.NotEqual(baseline, Id(engine: "diskann@1.0.0"));
        Assert.NotEqual(baseline, Id(chunker: "chunk@2"));
        Assert.NotEqual(baseline, Id(extractors: "extract@2"));
        Assert.NotEqual(baseline, Id(embedder: Embedder(model: "other")));
        Assert.NotEqual(baseline, Id(embedder: Embedder(dimensions: 512)));
    }

    [Fact]
    public void MissingIdentityMaterialIsRefused()
    {
        Assert.Throws<ArgumentException>(() => IndexVersionIds.Of(" ", "document@1", "flat@1", "chunk@1", "extract@1", Embedder(), []));
        Assert.Throws<ArgumentException>(() => IndexVersionIds.Of("contracts", "", "flat@1", "chunk@1", "extract@1", Embedder(), []));
        Assert.Throws<ArgumentException>(() => IndexVersionIds.Of("contracts", "document@1", " ", "chunk@1", "extract@1", Embedder(), []));
        Assert.Throws<ArgumentException>(() => IndexVersionIds.Of("contracts", "document@1", "flat@1", " ", "extract@1", Embedder(), []));
        Assert.Throws<ArgumentException>(() => IndexVersionIds.Of("contracts", "document@1", "flat@1", "chunk@1", "", Embedder(), []));
        Assert.Throws<ArgumentNullException>(() => IndexVersionIds.Of("contracts", "document@1", "flat@1", "chunk@1", "extract@1", null!, []));
        Assert.Throws<ArgumentNullException>(() => IndexVersionIds.Of("contracts", "document@1", "flat@1", "chunk@1", "extract@1", Embedder(), null!));
    }

    private static string Id(
        string collection = "contracts",
        string shape = "document@1",
        string engine = "flat@1",
        string chunker = "chunk@1",
        string extractors = "extract@1",
        EmbedderRef? embedder = null,
        IReadOnlyList<IndexedSource>? sources = null) =>
        IndexVersionIds.Of(
            collection,
            shape,
            engine,
            chunker,
            extractors,
            embedder ?? Embedder(),
            sources ?? [Source("src-1", "sha256:aa")]);

    private static EmbedderRef Embedder(string model = "local-hash@1", int dimensions = 256) => new("local", model, dimensions);

    private static IndexedSource Source(string sourceId, string contentHash) => new(sourceId, contentHash);
}
