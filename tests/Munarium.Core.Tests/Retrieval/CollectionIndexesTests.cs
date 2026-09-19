namespace Munarium.Core.Tests.Retrieval;

using Munarium.Core.Tests.Support;
using Munarium.Ledger;
using Munarium.Retrieval;

/// <summary>
/// Tests for resolving a collection to the reader that serves it: through the live version, by the name its manifest
/// records, and never through a registry of its own.
/// </summary>
public class CollectionIndexesTests
{
    /// <summary>A collection resolves to the reader of its own live version, which is what makes it a corpus.</summary>
    [Fact]
    public async Task ACollectionResolvesToTheReaderOfItsLiveVersion()
    {
        var host = new FakeIndexHost();
        var instance = host.Build("idx-live", SequenceNumber.Zero);
        var store = new InMemoryIndexVersionStore();
        await store.RegisterAsync(Version("idx-live", "contracts"));
        await store.ActivateAsync("acme", "col-contracts", "idx-live");
        var indexes = new CollectionIndexes(store, host, "acme");

        Assert.Same(instance.Reader, await indexes.ReaderForAsync("contracts"));
    }

    /// <summary>
    /// A collection with no live version has no reader, and that is not an error.
    /// </summary>
    /// <remarks>
    /// It is a collection a turn may read and cannot search, which is exactly what the original reports as skipped
    /// rather than as a failure - the difference between "this corpus is empty" and "I could not look".
    /// </remarks>
    [Fact]
    public async Task ACollectionWithNoLiveVersionHasNoReader()
    {
        var host = new FakeIndexHost();
        var store = new InMemoryIndexVersionStore();
        await store.RegisterAsync(Version("idx-registered", "minutes"));
        var indexes = new CollectionIndexes(store, host, "acme");

        // Registered but never activated: a version that exists and answers nothing yet.
        Assert.Null(await indexes.ReaderForAsync("minutes"));

        // And a collection nobody built at all.
        Assert.Null(await indexes.ReaderForAsync("nobody"));
    }

    /// <summary>A live version whose chunks are not in this process has no reader either.</summary>
    /// <remarks>
    /// Which is the case a restart creates and a cutover to it would refuse for the same reason: the version is the
    /// catalogue's, but the chunks are not this process's.
    /// </remarks>
    [Fact]
    public async Task ALiveVersionThisProcessNeverBuiltHasNoReader()
    {
        var store = new InMemoryIndexVersionStore();
        await store.RegisterAsync(Version("idx-elsewhere", "contracts"));
        await store.ActivateAsync("acme", "col-contracts", "idx-elsewhere");
        var indexes = new CollectionIndexes(store, new FakeIndexHost(), "acme");

        Assert.Null(await indexes.ReaderForAsync("contracts"));
    }

    /// <summary>The live collections are named by their manifests, ordered, and without a second lookup.</summary>
    [Fact]
    public async Task TheLiveCollectionsAreNamedByTheirManifests()
    {
        var host = new FakeIndexHost();
        var store = new InMemoryIndexVersionStore();

        foreach (var (id, collection) in new[] { ("idx-a", "minutes"), ("idx-b", "contracts") })
        {
            host.Build(id, SequenceNumber.Zero);
            await store.RegisterAsync(Version(id, collection));
            await store.ActivateAsync("acme", "col-" + collection, id);
        }

        var indexes = new CollectionIndexes(store, host, "acme");

        Assert.Equal(["contracts", "minutes"], await indexes.LiveCollectionsAsync());
    }

    /// <summary>One version, as the catalogue would hold it.</summary>
    /// <param name="id">The identity.</param>
    /// <param name="collection">The collection's name.</param>
    /// <returns>The version.</returns>
    private static IndexVersion Version(string id, string collection) => new()
    {
        Id = id,
        Tenant = "acme",
        CollectionId = "col-" + collection,
        ShapeRef = "cuad-contracts@3",
        Watermark = SequenceNumber.Zero,
        Manifest = new IndexManifest
        {
            CollectionId = "col-" + collection,
            CollectionName = collection,
            ShapeRef = "cuad-contracts@3",
            Engine = "exact@1",
            Chunker = "chunk@1",
            Extractors = "extract@1[docx@1]",
            Embedder = new EmbedderRef("local", "test-embedder", 3),
            SourceContentHashes = [],
            MaxChars = 1200,
        },
    };
}
