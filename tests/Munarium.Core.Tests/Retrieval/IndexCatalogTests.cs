namespace Munarium.Core.Tests.Retrieval;

using Munarium.Core.Tests.Support;
using Munarium.Ledger;
using Munarium.Retrieval;

/// <summary>
/// Tests for the catalogue: building records without serving, cutting over leaves one live version, and an answer's
/// envelope resolves back to the version that produced it.
/// </summary>
public class IndexCatalogTests
{
    [Fact]
    public async Task ABuildRecordsAVersionWithoutMakingItLive()
    {
        var store = new InMemoryIndexVersionStore();
        var catalog = new IndexCatalog(store);

        var version = await catalog.RegisterAsync(Request(watermark: 10));

        Assert.StartsWith("idx-", version.Id, StringComparison.Ordinal);
        Assert.False(version.Active);
        Assert.Equal(1, store.Count);
        Assert.Null(await catalog.ActiveAsync("demo", "contracts"));
    }

    /// <summary>
    /// A rebuild of the same corpus is the same version: the identity hashes what was built, so a caller can build
    /// without first asking whether it already did.
    /// </summary>
    [Fact]
    public async Task ARebuildOfTheSameCorpusIsTheSameVersion()
    {
        var store = new InMemoryIndexVersionStore();
        var catalog = new IndexCatalog(store);

        var first = await catalog.RegisterAsync(Request(watermark: 10));
        var second = await catalog.RegisterAsync(Request(watermark: 10));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, store.Count);
    }

    /// <summary>A rebuild may advance the watermark, because it read a later ledger state, and never move it back.</summary>
    [Fact]
    public async Task ARebuildAdvancesTheWatermarkButNeverBackwards()
    {
        var catalog = new IndexCatalog(new InMemoryIndexVersionStore());

        await catalog.RegisterAsync(Request(watermark: 10));

        Assert.Equal(25, (await catalog.RegisterAsync(Request(watermark: 25))).Watermark.Value);
        Assert.Equal(25, (await catalog.RegisterAsync(Request(watermark: 15))).Watermark.Value);
    }

    [Fact]
    public async Task CuttingOverLeavesExactlyOneLiveVersionAndKeepsTheOldOneResolvable()
    {
        var catalog = new IndexCatalog(new InMemoryIndexVersionStore());

        var first = await catalog.RegisterAsync(Request(watermark: 10, activate: true, hashes: ["sha256:aa"]));
        Assert.True(first.Active);
        Assert.NotNull(first.ActivatedAt);

        var second = await catalog.RegisterAsync(Request(watermark: 12, activate: true, hashes: ["sha256:bb"]));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, (await catalog.ActiveAsync("demo", "contracts"))?.Id);

        // The superseded version is still there, still readable, and no longer serving.
        var superseded = (await catalog.ResolveAsync("demo", Envelope(first.Id, 10, ["sha256:aa"]))).Version;

        Assert.NotNull(superseded);
        Assert.True(superseded.Superseded);
        Assert.False(superseded.Active);
        Assert.NotNull(superseded.DeactivatedAt);
    }

    /// <summary>When a version started serving is a fact about the version, not about the call that made it live again.</summary>
    [Fact]
    public async Task ReactivatingASupersededVersionKeepsWhenItFirstServed()
    {
        var catalog = new IndexCatalog(new InMemoryIndexVersionStore());

        var first = await catalog.RegisterAsync(Request(watermark: 10, activate: true, hashes: ["sha256:aa"]));
        await catalog.RegisterAsync(Request(watermark: 12, activate: true, hashes: ["sha256:bb"]));

        var reactivated = await catalog.ActivateAsync("demo", "contracts", first.Id);

        Assert.NotNull(reactivated);
        Assert.True(reactivated.Active);
        Assert.Equal(first.ActivatedAt, reactivated.ActivatedAt);
        Assert.Null(reactivated.DeactivatedAt);
        Assert.Equal(first.Id, (await catalog.ActiveAsync("demo", "contracts"))?.Id);
    }

    [Fact]
    public async Task ANamedVersionThatIsNotInTheCollectionIsNotActivated() =>
        Assert.Null(await new IndexCatalog(new InMemoryIndexVersionStore())
            .ActivateAsync("demo", "contracts", "idx-0123456789abcdef"));

    [Fact]
    public async Task ABuildWithoutSourcesIsRefused() =>
        await Assert.ThrowsAsync<ArgumentException>(async () => await new IndexCatalog(new InMemoryIndexVersionStore())
            .RegisterAsync(Request(watermark: 1) with { Sources = [] }));

    /// <summary>Chunk, envelope, index version, ledger position: the whole chain, resolvable after the fact.</summary>
    [Fact]
    public async Task AnAnswerResolvesToTheIndexVersionThatProducedIt()
    {
        var catalog = new IndexCatalog(new InMemoryIndexVersionStore());
        var version = await catalog.RegisterAsync(Request(watermark: 42, activate: true, hashes: ["sha256:aa"]));

        var resolution = await catalog.ResolveAsync("demo", Envelope(version.Id, 42, ["sha256:aa"]));

        Assert.True(resolution.Resolved);
        Assert.Null(resolution.Failure);
        Assert.Equal(version.Id, resolution.Version?.Id);
        Assert.Equal(new SequenceNumber(42), resolution.Version?.Watermark);
        Assert.Empty(resolution.UnrecordedContentHashes);
    }

    /// <summary>An answer may be older than a rebuild that advanced the version, and never newer than the version's own position.</summary>
    [Fact]
    public async Task AnAnswerFromBeforeARebuildStillResolves()
    {
        var catalog = new IndexCatalog(new InMemoryIndexVersionStore());
        var version = await catalog.RegisterAsync(Request(watermark: 10, activate: true, hashes: ["sha256:aa"]));
        await catalog.RegisterAsync(Request(watermark: 30, hashes: ["sha256:aa"]));

        Assert.True((await catalog.ResolveAsync("demo", Envelope(version.Id, 10, ["sha256:aa"]))).Resolved);
    }

    /// <summary>A version records the bytes it indexed, so an answer citing bytes outside that set cannot have come from it.</summary>
    [Fact]
    public async Task AnAnswerCitingBytesTheVersionNeverHeldIsRefused()
    {
        var catalog = new IndexCatalog(new InMemoryIndexVersionStore());
        var version = await catalog.RegisterAsync(Request(watermark: 10, activate: true, hashes: ["sha256:aa"]));

        var resolution = await catalog.ResolveAsync("demo", Envelope(version.Id, 10, ["sha256:aa", "sha256:cc"]));

        Assert.False(resolution.Resolved);
        Assert.Equal(["sha256:cc"], resolution.UnrecordedContentHashes);
        Assert.Equal("the answer cites bytes this index version never held", resolution.Failure);
    }

    [Fact]
    public async Task AnAnswerCannotClaimMoreOfTheLedgerThanItsIndexReflected()
    {
        var catalog = new IndexCatalog(new InMemoryIndexVersionStore());
        var version = await catalog.RegisterAsync(Request(watermark: 10, activate: true, hashes: ["sha256:aa"]));

        var resolution = await catalog.ResolveAsync("demo", Envelope(version.Id, 20, ["sha256:aa"]));

        Assert.False(resolution.Resolved);
        Assert.Equal(
            "the answer claims a ledger position this index version never reflected",
            resolution.Failure);
    }

    [Fact]
    public async Task AnEnvelopeNamingAnUnknownIndexVersionCannotBeResolved()
    {
        var resolution = await new IndexCatalog(new InMemoryIndexVersionStore())
            .ResolveAsync("demo", Envelope("idx-0123456789abcdef", 1, ["sha256:aa"]));

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Version);
        Assert.Equal("index version 'idx-0123456789abcdef' cannot be resolved", resolution.Failure);
    }

    [Fact]
    public async Task AVersionsOwnResolutionIsUnaffectedByAnotherTenantsVersion()
    {
        var catalog = new IndexCatalog(new InMemoryIndexVersionStore());
        var mine = await catalog.RegisterAsync(Request(watermark: 5, hashes: ["sha256:aa"]));

        var resolution = await catalog.ResolveAsync("other-tenant", Envelope(mine.Id, 5, ["sha256:aa"]));

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Version);
    }

    private static IndexBuildRequest Request(
        long watermark,
        bool activate = false,
        IReadOnlyList<string>? hashes = null,
        string collectionId = "contracts") => new()
        {
            Tenant = "demo",
            Manifest = new IndexManifest
            {
                CollectionId = collectionId,
                CollectionName = "Contracts",
                ShapeRef = "document@1",
                Engine = "flat@1",
                Chunker = "chunk@1",
                Extractors = "extract@1",
                Embedder = new EmbedderRef("local", "local-hash@1", 256),
                SourceContentHashes = hashes ?? ["sha256:aa"],
                MaxChars = 1_200,
            },
            Sources = [.. (hashes ?? ["sha256:aa"]).Select((hash, index) => new IndexedSource($"src-{index}", hash))],
            Watermark = new SequenceNumber(watermark),
            Activate = activate,
        };

    private static ProvenanceEnvelope Envelope(string indexVersion, long watermark, IReadOnlyList<string> hashes) => new(
        indexVersion,
        new SequenceNumber(watermark),
        [
            .. hashes.Select((hash, index) => new SourceReference(
                $"chunk-{index}",
                $"src-{index}",
                "docs/policy.pdf",
                hash,
                ChunkOrdinal: 0)),
        ]);
}
