namespace Munarium.Core.Tests.Retrieval;

using Munarium.Retrieval;

/// <summary>
/// Tests for writing an index version's manifest down and reading it back: what a version was built from has to
/// survive the round trip unchanged, or a rebuild would silently become a different version.
/// </summary>
public class IndexManifestCodecTests
{
    [Fact]
    public void AManifestSurvivesTheRoundTrip()
    {
        var manifest = Manifest();

        var read = IndexManifestCodec.FromJson(IndexManifestCodec.ToJson(manifest), "test manifest");

        Assert.Equal(manifest.CollectionId, read.CollectionId);
        Assert.Equal(manifest.CollectionName, read.CollectionName);
        Assert.Equal(manifest.ShapeRef, read.ShapeRef);
        Assert.Equal(manifest.Engine, read.Engine);
        Assert.Equal(manifest.Chunker, read.Chunker);
        Assert.Equal(manifest.Extractors, read.Extractors);
        Assert.Equal(manifest.MaxChars, read.MaxChars);
        Assert.Equal(manifest.Embedder, read.Embedder);
        Assert.Equal(manifest.SourceContentHashes, read.SourceContentHashes);
    }

    /// <summary>
    /// A field written by a later build is ignored rather than fatal: a row written yesterday has to stay readable, or
    /// every deployment would have to migrate its history to read its own past.
    /// </summary>
    [Fact]
    public void AFieldThatIsNotKnownIsIgnored()
    {
        var json = IndexManifestCodec.ToJson(Manifest()).Replace(
            "\"chunker\":",
            "\"something_new\": {\"a\": 1}, \"chunker\":",
            StringComparison.Ordinal);

        var read = IndexManifestCodec.FromJson(json, "test manifest");

        Assert.Equal("chunk@1", read.Chunker);
    }

    [Fact]
    public void AManifestMissingWhatItNeedsIsRefused()
    {
        var json = IndexManifestCodec.ToJson(Manifest()).Replace("\"engine\":\"exact@1\",", string.Empty, StringComparison.Ordinal);

        var refusal = Assert.Throws<FormatException>(
            () => IndexManifestCodec.FromJson(json, "test manifest"));

        Assert.Contains("engine", refusal.Message, StringComparison.Ordinal);
    }

    private static IndexManifest Manifest() => new()
    {
        CollectionId = "col-docs",
        CollectionName = "Documents",
        ShapeRef = "vendor@1",
        Engine = "exact@1",
        Chunker = "chunk@1",
        Extractors = "text@1",
        MaxChars = 1200,
        Embedder = new EmbedderRef("local", "munarium-deterministic-v1", 256),
        SourceContentHashes = ["sha256:aaa", "sha256:bbb"],
    };
}
