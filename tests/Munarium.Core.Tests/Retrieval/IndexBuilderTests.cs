namespace Munarium.Core.Tests.Retrieval;

using System.Text;
using Munarium.Core.Tests.Support;
using Munarium.Evidence;
using Munarium.Ledger;
using Munarium.Retrieval;
using Munarium.Sources;
using Munarium.Text;

/// <summary>
/// Tests for building an index version: what reaches the index, what is recorded, and what happens to serving while it
/// happens.
/// </summary>
public class IndexBuilderTests
{
    private const string Document =
        "The Bell rang twice.\n\nA second paragraph, longer than the first, with a sentence in it.\n\nA closing one.";

    /// <summary>
    /// A build indexes the sources a collection binds - only those - and every chunk it writes carries a citation that
    /// resolves to the path and the hash the document was stored under.
    /// </summary>
    [Fact]
    public async Task ABuildIndexesTheBoundSourcesAndCitesThem()
    {
        var fixture = Fixture();
        await BindAsync(fixture, "docs/a.txt", Document);
        await BindAsync(fixture, "docs/b.txt", "The Bell rang three times.");
        await BindAsync(fixture, "other/c.txt", "A document no collection bound.");

        var version = Recorded(await fixture.Builder.BuildAsync(Plan(prefix: "docs/")));

        Assert.StartsWith(IndexVersionIds.Prefix, version.Id, StringComparison.Ordinal);
        Assert.False(version.Active);

        var instance = fixture.Host.Instances[version.Id];
        var chunks = ((RecordingIndexWriter)instance.Writer).Chunks;

        Assert.True(chunks.Count > 3, "both bound documents should have been chunked");
        Assert.All(chunks, chunk => Assert.StartsWith("docs/", chunk.Source.SourcePath, StringComparison.Ordinal));
        Assert.All(chunks, chunk => Assert.StartsWith(chunk.Source.SourceId, chunk.Source.ChunkId, StringComparison.Ordinal));
        Assert.Equal(2, version.Manifest.SourceContentHashes.Count);
        Assert.Equal("chunk@1", version.Manifest.Chunker);
        Assert.Equal(TextExtractor.Version(), version.Manifest.Extractors);
        Assert.Equal("exact@1", version.Manifest.Engine);
        Assert.Equal("test-model", version.Manifest.Embedder.Model);
    }

    /// <summary>
    /// A build nobody activated is not a build that answers: the version exists, its instance is kept so a cutover can
    /// serve it, and the questions still go to the version that was serving.
    /// </summary>
    [Fact]
    public async Task ABuildNobodyActivatedDoesNotServe()
    {
        var fixture = Fixture();
        await BindAsync(fixture, "docs/a.txt", Document);

        var version = Recorded(await fixture.Builder.BuildAsync(Plan(prefix: "docs/")));

        Assert.False(version.Active);
        Assert.Equal("munarium@1", fixture.Host.ServingVersion);
        Assert.Equal([version.Id], fixture.Host.Built);
    }

    /// <summary>
    /// An activated build cuts the collection over, and the version records the ledger position it was built against:
    /// without that, a citation would prove which bytes were read and not which state of the world was believed.
    /// </summary>
    [Fact]
    public async Task AnActivatedBuildCutsTheCollectionOver()
    {
        var fixture = Fixture();
        await BindAsync(fixture, "docs/a.txt", Document);

        var version = Recorded(await fixture.Builder.BuildAsync(Plan(prefix: "docs/", watermark: 42, activate: true)));

        Assert.True(version.Active);
        Assert.Equal(42, version.Watermark.Value);
        Assert.Equal(version.Id, fixture.Host.ServingVersion);
        Assert.Equal(version.Id, (await fixture.Versions.ActiveAsync("acme", "col-docs"))?.Id);
    }

    [Fact]
    public async Task ABuildWithNothingBoundIsRefusedAndBuildsNothing()
    {
        var fixture = Fixture();
        await BindAsync(fixture, "other/a.txt", Document);

        var refused = Refused(await fixture.Builder.BuildAsync(Plan(prefix: "docs/")));

        Assert.Contains("docs/", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Host.Built);
        Assert.Equal(0, fixture.Versions.Count);
    }

    /// <summary>
    /// A bound document this port cannot read refuses the build rather than being dropped from it - and the instance
    /// built so far is dropped too, so a half-built corpus cannot be activated by name later.
    /// </summary>
    [Fact]
    public async Task ADocumentNoExtractorReadsRefusesTheBuildAndDropsTheInstance()
    {
        var fixture = Fixture();
        await BindAsync(fixture, "docs/a.txt", Document, "text/plain");
        await BindAsync(fixture, "docs/plate.tiff", "II*\u0004", "image/tiff");

        var refused = Refused(await fixture.Builder.BuildAsync(Plan(prefix: "docs/", activate: true)));

        Assert.Contains("docs/plate.tiff", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("image/tiff", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Host.Built);
        Assert.Equal("munarium@1", fixture.Host.ServingVersion);
        Assert.Equal(0, fixture.Versions.Count);
    }

    /// <summary>
    /// A document in a format this port reads, over bytes that are not that format, is recorded as a failed
    /// extraction and contributes nothing: the build does not stop, because the row says what happened.
    /// </summary>
    /// <remarks>
    /// This is the original's stance, and the reason its rows carry these two fields at all: an extraction that
    /// failed is data, and a corpus quietly missing one document is visible in the rows rather than only in a log.
    /// </remarks>
    [Fact]
    public async Task ADocumentThatIsNotTheFormatItDeclaresIsRecordedAsFailed()
    {
        var fixture = Fixture();
        await BindAsync(fixture, "docs/notes.docx", "definitely not a zip", DocxExtractor.Media);
        await BindAsync(fixture, "docs/a.txt", Document, "text/plain");

        var version = Recorded(await fixture.Builder.BuildAsync(Plan(prefix: "docs/", activate: true)));

        // Both rows are accounted for in the manifest, and every chunk that was written came from the good one.
        Assert.Equal(2, version.Manifest.SourceContentHashes.Count);
        Assert.All(
            ((RecordingIndexWriter)fixture.Host.Instances[version.Id].Writer).Chunks,
            chunk => Assert.Equal("docs/a.txt", chunk.Source.SourcePath));

        var failed = await fixture.Registry.FindAsync("acme", "docs/notes.docx");

        Assert.Equal("failed", failed?.ExtractionStatus);
        Assert.Equal("docx", failed?.ExtractionMethod);

        var good = await fixture.Registry.FindAsync("acme", "docs/a.txt");

        Assert.Equal("ok", good?.ExtractionStatus);
        Assert.Equal("text", good?.ExtractionMethod);
    }

    /// <summary>
    /// A document of nothing but whitespace is accounted for - its hash is in the manifest, so the version says what it
    /// holds - but it contributes no chunks, because a chunk of newlines is not evidence.
    /// </summary>
    [Fact]
    public async Task AWhitespaceOnlyDocumentIsAccountedForAndIndexesNothing()
    {
        var fixture = Fixture();
        await BindAsync(fixture, "docs/blank.txt", "   \n\n\t ");
        await BindAsync(fixture, "docs/a.txt", "The Bell rang twice.");

        var version = Recorded(await fixture.Builder.BuildAsync(Plan(prefix: "docs/")));

        Assert.Equal(2, version.Manifest.SourceContentHashes.Count);
        Assert.Equal(1, fixture.Host.Instances[version.Id].Writer.Count);
    }

    /// <summary>
    /// The chunks are written into the version that is recorded: the identity is derived before the build and the
    /// catalogue derives the same one from the same material, so the row names the corpus the chunks came from.
    /// </summary>
    [Fact]
    public async Task TheVersionTheChunksWereWrittenIntoIsTheVersionThatIsRecorded()
    {
        var fixture = Fixture();
        await BindAsync(fixture, "docs/a.txt", Document);

        var version = Recorded(await fixture.Builder.BuildAsync(Plan(prefix: "docs/")));

        var stored = await fixture.Versions.GetAsync("acme", version.Id);

        Assert.NotNull(stored);
        Assert.Equivalent(version.Manifest, stored.Manifest);
        Assert.Equal(version.Id, Assert.Single(fixture.Host.Built));
        Assert.True(fixture.Host.Instances[version.Id].Writer.Count > 0);
    }

    /// <summary>
    /// Rebuilding the same corpus under the same policies <em>is</em> the same version: the identity is content-addressed,
    /// which is what makes a build idempotent and a cutover meaningful.
    /// </summary>
    [Fact]
    public async Task RebuildingTheSameCorpusIsTheSameVersion()
    {
        var fixture = Fixture();
        await BindAsync(fixture, "docs/a.txt", Document);

        var first = Recorded(await fixture.Builder.BuildAsync(Plan(prefix: "docs/")));
        var second = Recorded(await fixture.Builder.BuildAsync(Plan(prefix: "docs/", watermark: 9)));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, fixture.Versions.Count);
        Assert.Equal(9, second.Watermark.Value);
        Assert.Equal([first.Id, first.Id], fixture.Host.Built);
    }

    [Fact]
    public async Task ABuildWithNoCeilingOrNoEmbedderIsRefused()
    {
        var fixture = Fixture();

        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexBuilder(
            fixture.Store,
            fixture.Registry,
            fixture.Provider,
            fixture.Host,
            new IndexCatalog(fixture.Versions),
            new EmbedderRef("local", "test-model", 4),
            maxChunkChars: 0));

        await Task.CompletedTask;
    }

    private static (IndexBuilder Builder,
        FakeIndexHost Host,
        InMemorySourceStore Store,
        InMemorySourceRegistry Registry,
        InMemoryIndexVersionStore Versions,
        RecordingEmbeddingProvider Provider) Fixture()
    {
        var store = new InMemorySourceStore();
        var registry = new InMemorySourceRegistry();
        var host = new FakeIndexHost();
        var versions = new InMemoryIndexVersionStore();
        var provider = new RecordingEmbeddingProvider();

        var builder = new IndexBuilder(
            store,
            registry,
            provider,
            host,
            new IndexCatalog(versions),
            new EmbedderRef("local", "test-model", 4),
            maxChunkChars: 30);

        return (builder, host, store, registry, versions, provider);
    }

    private static async Task BindAsync(
        (IndexBuilder Builder,
            FakeIndexHost Host,
            InMemorySourceStore Store,
            InMemorySourceRegistry Registry,
            InMemoryIndexVersionStore Versions,
            RecordingEmbeddingProvider Provider) fixture,
        string path,
        string text,
        string mediaType = "text/plain")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = ArtifactContent.Hash(bytes);

        await fixture.Store.PutAsync(SourceKey.New("acme", path, hash), mediaType, bytes);
        await fixture.Registry.RecordAsync(new SourceRecord
        {
            Tenant = "acme",
            SourceId = SourceKey.Id("acme", path),
            Path = path,
            ContentHash = hash,
            MediaType = mediaType,
            BytesLength = bytes.Length,
            BlobUri = $"mem://acme/{path}",
            BackendId = "mem",
        });
    }

    private static IndexBuildPlan Plan(string? prefix, long watermark = 0, bool activate = false) => new()
    {
        Tenant = "acme",
        CollectionId = "col-docs",
        CollectionName = "Documents",
        ShapeRef = "vendor@1",
        PathPrefix = prefix,
        Watermark = new SequenceNumber(watermark),
        Activate = activate,
    };

    private static IndexVersion Recorded(IndexBuildOutcome outcome) =>
        outcome is IndexVersion version ? version : throw new InvalidOperationException("Nothing was built.");

    private static IndexBuildRefused Refused(IndexBuildOutcome outcome) =>
        outcome is IndexBuildRefused refused ? refused : throw new InvalidOperationException("Something was built.");
}
