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
        var id = IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa")]);

        Assert.StartsWith("idx-", id, StringComparison.Ordinal);
        Assert.Equal(20, id.Length);
        Assert.Equal(id, IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa")]));
    }

    /// <summary>
    /// A caller's ordering is not identity material: the same corpus bound in a different order is the same corpus,
    /// and an identity that flipped with insertion order would rebuild an index nobody changed.
    /// </summary>
    [Fact]
    public void TheOrderSourcesAreBoundInIsNotIdentityMaterial() =>
        Assert.Equal(
            IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa"), Source("src-2", "sha256:bb")]),
            IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-2", "sha256:bb"), Source("src-1", "sha256:aa")]));

    /// <summary>
    /// The bug a unit separator closes, the same one the evidence plane's domain key learned: material joined with
    /// a character that can also appear in the material can be reached two ways, and two different corpora would
    /// then share one identity.
    /// </summary>
    [Fact]
    public void OneSourceIdHoldingTheSeparatorIsNotTwoSources() =>
        Assert.NotEqual(
            IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1\u001fsha256:aa", "sha256:aa")]),
            IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa")]));

    /// <summary>Two sources that happen to share bytes are still two sources, and one path re-put with new bytes is another corpus.</summary>
    [Fact]
    public void IdentityPairsTheSourceWithItsBytes()
    {
        Assert.NotEqual(
            IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa")]),
            IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa"), Source("src-2", "sha256:aa")]));

        Assert.NotEqual(
            IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa")]),
            IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:bb")]));
    }

    /// <summary>
    /// Every change that alters the text or the vectors has to mint a new version - including the extractor, which
    /// changes the text for identical bytes, and the embedder, which changes what a query matches.
    /// </summary>
    [Fact]
    public void EveryChangeThatAltersTheTextOrTheVectorsMintsANewVersion()
    {
        var baseline = IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa")]);

        Assert.NotEqual(baseline, IndexVersionIds.Of("other", "document@1", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa")]));
        Assert.NotEqual(baseline, IndexVersionIds.Of("contracts", "document@2", "chunk@1", "extract@1", Embedder(), [Source("src-1", "sha256:aa")]));
        Assert.NotEqual(baseline, IndexVersionIds.Of("contracts", "document@1", "chunk@2", "extract@1", Embedder(), [Source("src-1", "sha256:aa")]));
        Assert.NotEqual(baseline, IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@2", Embedder(), [Source("src-1", "sha256:aa")]));
        Assert.NotEqual(baseline, IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(model: "other"), [Source("src-1", "sha256:aa")]));
        Assert.NotEqual(baseline, IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(dimensions: 512), [Source("src-1", "sha256:aa")]));
    }

    [Fact]
    public void MissingIdentityMaterialIsRefused()
    {
        Assert.Throws<ArgumentException>(() => IndexVersionIds.Of(" ", "document@1", "chunk@1", "extract@1", Embedder(), []));
        Assert.Throws<ArgumentException>(() => IndexVersionIds.Of("contracts", "", "chunk@1", "extract@1", Embedder(), []));
        Assert.Throws<ArgumentException>(() => IndexVersionIds.Of("contracts", "document@1", " ", "extract@1", Embedder(), []));
        Assert.Throws<ArgumentException>(() => IndexVersionIds.Of("contracts", "document@1", "chunk@1", "", Embedder(), []));
        Assert.Throws<ArgumentNullException>(() => IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", null!, []));
        Assert.Throws<ArgumentNullException>(() => IndexVersionIds.Of("contracts", "document@1", "chunk@1", "extract@1", Embedder(), null!));
    }

    private static EmbedderRef Embedder(string model = "local-hash@1", int dimensions = 256) => new("local", model, dimensions);

    private static IndexedSource Source(string sourceId, string contentHash) => new(sourceId, contentHash);
}
