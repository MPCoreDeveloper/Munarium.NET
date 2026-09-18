namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Ledger;
using Munarium.Retrieval;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbIndexVersionStore"/>: the rows that say which index answered, and the two rules a
/// rebuild and a cutover have to obey.
/// </summary>
public class SharpCoreDbIndexVersionStoreTests
{
    [Fact]
    public async Task AVersionIsReadBackByItsIdentity()
    {
        await using var fixture = SourceStoreFixture.Create();

        var recorded = await fixture.IndexVersions.RegisterAsync(Version("idx-one", 3));

        // A record holding a list compares that list by reference, so the manifest is compared structurally.
        Assert.Equivalent(Version("idx-one", 3).Manifest, recorded.Manifest);
        Assert.False(recorded.Active, "a version nobody has activated is not live");
        Assert.Null(recorded.ActivatedAt);

        var read = await fixture.IndexVersions.GetAsync("acme", "idx-one");

        Assert.NotNull(read);
        Assert.Equal(3, read.Watermark.Value);
        Assert.Equal("col-docs", read.CollectionId);
        Assert.Equal("exact@1", read.Manifest.Engine);
        Assert.Equal(["sha256:aaa", "sha256:bbb"], read.Manifest.SourceContentHashes);
        Assert.Equal(new EmbedderRef("local", "munarium-deterministic-v1", 256), read.Manifest.Embedder);
    }

    [Fact]
    public async Task AnUnknownVersionIsNotThere()
    {
        await using var fixture = SourceStoreFixture.Create();

        Assert.Null(await fixture.IndexVersions.GetAsync("acme", "idx-missing"));
        Assert.Null(await fixture.IndexVersions.ActiveAsync("acme", "col-docs"));
        Assert.Null(await fixture.IndexVersions.GetAsync("other", "idx-missing"));
    }

    /// <summary>
    /// An identity fixes what a version is, so a second registration is not a rewrite: the manifest stays as recorded
    /// and the watermark may only move forwards, because a rebuild reads a later ledger state and can never un-read it.
    /// </summary>
    [Fact]
    public async Task AnIdentityIsNotRewrittenAndTheWatermarkOnlyMovesForwards()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.IndexVersions.RegisterAsync(Version("idx-two", 5));

        var backwards = await fixture.IndexVersions.RegisterAsync(
            Version("idx-two", 3) with { Manifest = Another() });

        Assert.Equal(5, backwards.Watermark.Value);
        Assert.Equal("exact@1", backwards.Manifest.Engine);

        var forwards = await fixture.IndexVersions.RegisterAsync(Version("idx-two", 7) with { Manifest = Another() });

        Assert.Equal(7, forwards.Watermark.Value);
        Assert.Equal("exact@1", forwards.Manifest.Engine);
        Assert.Equal("idx-two", forwards.Id);
    }

    /// <summary>
    /// A cutover is a serving decision: exactly one version is live per collection, and the one that stopped serving
    /// keeps the instant it stopped, so how long it was live stays answerable and its envelopes still resolve.
    /// </summary>
    [Fact]
    public async Task ACutoverLeavesExactlyOneLiveVersionAndKeepsTheOtherReadable()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.IndexVersions.RegisterAsync(Version("idx-first", 1));
        await fixture.IndexVersions.RegisterAsync(Version("idx-second", 2));

        var first = await fixture.IndexVersions.ActivateAsync("acme", "col-docs", "idx-first");
        var second = await fixture.IndexVersions.ActivateAsync("acme", "col-docs", "idx-second");

        Assert.True(first?.Active);
        Assert.True(second?.Active);

        var live = await fixture.IndexVersions.ActiveAsync("acme", "col-docs");
        Assert.Equal("idx-second", live?.Id);

        var superseded = await fixture.IndexVersions.GetAsync("acme", "idx-first");

        Assert.NotNull(superseded);
        Assert.False(superseded.Active);
        Assert.NotNull(superseded.DeactivatedAt);
        Assert.True(superseded.Superseded);
    }

    [Fact]
    public async Task AVersionsOwnCollectionIsTheOnlyOneItCanBeActivatedInto()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.IndexVersions.RegisterAsync(Version("idx-scoped", 1));

        Assert.Null(await fixture.IndexVersions.ActivateAsync("acme", "col-other", "idx-scoped"));
        Assert.Null(await fixture.IndexVersions.ActivateAsync("acme", "col-docs", "idx-missing"));
        Assert.Null(await fixture.IndexVersions.ActiveAsync("acme", "col-docs"));
    }

    [Fact]
    public async Task OneTenantCannotSeeAnothersVersions()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.IndexVersions.RegisterAsync(Version("idx-tenant", 1));

        Assert.Null(await fixture.IndexVersions.GetAsync("other", "idx-tenant"));
        Assert.Null(await fixture.IndexVersions.ActivateAsync("other", "col-docs", "idx-tenant"));
    }

    /// <summary>
    /// The versions outlive the connection that wrote them: an envelope issued yesterday has to resolve today, and it
    /// resolves through this table.
    /// </summary>
    [Fact]
    public async Task TheVersionsAreStillThereAfterTheDatabaseIsReopened()
    {
        var fixture = SourceStoreFixture.Create();
        var path = fixture.DatabasePath;

        var recorded = await fixture.IndexVersions.RegisterAsync(Version("idx-persistent", 4));
        var first = await fixture.IndexVersions.ActivateAsync("acme", "col-docs", "idx-persistent");

        // Superseded by another version, then brought back: the instant it first went live stays.
        await fixture.IndexVersions.RegisterAsync(Version("idx-later", 5));
        await fixture.IndexVersions.ActivateAsync("acme", "col-docs", "idx-later");
        var again = await fixture.IndexVersions.ActivateAsync("acme", "col-docs", recorded.Id);

        Assert.Equal(first?.ActivatedAt, again?.ActivatedAt);

        await fixture.DisposeAsync();

        await using var reopened = SourceStoreFixture.Create(path);

        var live = await reopened.IndexVersions.ActiveAsync("acme", "col-docs");

        Assert.Equal("idx-persistent", live?.Id);
        Assert.True(live?.Active);
        Assert.NotNull(live?.ActivatedAt);
    }

    private static IndexVersion Version(string id, long watermark) => new()
    {
        Id = id,
        Tenant = "acme",
        CollectionId = "col-docs",
        ShapeRef = "vendor@1",
        Watermark = new SequenceNumber(watermark),
        Manifest = new IndexManifest
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
        },
    };

    private static IndexManifest Another() => new()
    {
        CollectionId = "col-docs",
        CollectionName = "Documents",
        ShapeRef = "vendor@1",
        Engine = "diskann@1",
        Chunker = "chunk@1",
        Extractors = "text@1",
        MaxChars = 1200,
        Embedder = new EmbedderRef("local", "munarium-deterministic-v1", 256),
        SourceContentHashes = ["sha256:ccc"],
    };
}
