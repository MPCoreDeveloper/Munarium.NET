namespace Munarium.Core.Tests.Evidence;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Evidence;
using Munarium.Facts;
using Munarium.Governance;

/// <summary>
/// The fact plane: a pinned slice of the ledger's own accepted facts.
/// </summary>
/// <remarks>
/// The provider is exercised over the real write path rather than a stand-in, because what it reads is what a turn
/// would be answered with: the facts that are current at a pin, projected into the claims the gates reason over.
/// </remarks>
public class FactProviderTests
{
    [Fact]
    public void ItServesOnlyFactQualifiedSources()
    {
        var provider = new FactProvider(new FactLedger(new FakeStorageBackend()));

        Assert.Equal("facts", provider.Id);
        Assert.True(provider.CanServe("facts:candidates/1"));
        Assert.False(provider.CanServe("contracts"));
        Assert.False(provider.CanServe("matrix:revenue_by_region"));
    }

    [Fact]
    public async Task ALayerWithoutAVersionRefusesRatherThanReadingSomething()
    {
        var provider = new FactProvider(new FactLedger(new FakeStorageBackend()));

        var block = await provider.FetchAsync(Layer(["contracts"]), Intent());

        var refusal = RefusalOf(block);

        Assert.Equal(EvidenceRefusalCodes.SourceNotBound, refusal.Code);

        // No source is named: the layer's own sources are what is wrong, and the hidden-source rule protects sources
        // a caller cannot see - naming this one would be noise, not disclosure.
        Assert.Null(refusal.Source);
    }

    [Fact]
    public async Task APinnedVersionComesBackAsTheFactsThatAreCurrent()
    {
        var storage = new FakeStorageBackend();
        var candidates = new CandidateLedger(storage, new MeshSnapshotBuilder(storage));
        var provider = new FactProvider(new FactLedger(storage));

        await candidates.AppendAsync(
            "candidates/1",
            [
                CandidateFixture.Proposal("vendor-north", "status", "preferred"),
                CandidateFixture.Proposal("vendor-south", "status", "probation"),
            ]);

        // The same lineage, corrected. A slice shows one fact per lineage: the correction, not both.
        await candidates.AppendAsync(
            "candidates/1",
            [CandidateFixture.Proposal("vendor-south", "status", "cleared")]);

        var block = await provider.FetchAsync(Layer(["facts:candidates/1"]), Intent());

        var slice = FactsOf(block);

        Assert.Equal(
            ["vendor-north", "vendor-south"],
            slice.Claims.Select(claim => claim.Subject));
        Assert.Equal(
            ["preferred", "cleared"],
            slice.Claims.Select(claim => claim.Value));

        // The claims carry their ledger position, because that is the axis the pin is on and the order a composition
        // reads them in.
        Assert.Equal([1, 3], slice.Claims.Select(claim => claim.Sequence.Value));
    }

    [Fact]
    public async Task AScopePrefixNarrowsTheSliceToThatBranch()
    {
        var storage = new FakeStorageBackend();
        var candidates = new CandidateLedger(storage, new MeshSnapshotBuilder(storage));
        var provider = new FactProvider(new FactLedger(storage));

        await candidates.AppendAsync(
            "candidates/1",
            [
                CandidateFixture.Proposal("vendor-north", "status", "preferred", scope: "vendor.north"),
                CandidateFixture.Proposal("vendor-south", "status", "probation", scope: "vendor.south"),

                // Unscoped, and therefore outside every scoped layer. A scope prefix is a branch of the tree, not a
                // filter that widens when nothing matches it.
                CandidateFixture.Proposal("vendor-east", "status", "preferred"),
            ]);

        var narrowed = FactsOf(
            await provider.FetchAsync(Layer(["facts:candidates/1", "scope:vendor.north"]), Intent()));

        Assert.Equal("vendor-north", Assert.Single(narrowed.Claims).Subject);

        var elsewhere = FactsOf(
            await provider.FetchAsync(Layer(["facts:candidates/1", "scope:vendor.nowhere"]), Intent()));

        Assert.Empty(elsewhere.Claims);

        // Without a prefix the whole version comes back, which is the same read the layer above narrowed.
        var whole = FactsOf(await provider.FetchAsync(Layer(["facts:candidates/1"]), Intent()));

        Assert.Equal(3, whole.Claims.Count);
    }

    private static LedgerFactSlice FactsOf(EvidenceBlock block) =>
        block is LedgerFactSlice slice
            ? slice
            : throw new Xunit.Sdk.XunitException($"expected a fact slice, got {block.KindName()}");

    private static EvidenceRefusal RefusalOf(EvidenceBlock block) =>
        block is EvidenceRefusal refusal
            ? refusal
            : throw new Xunit.Sdk.XunitException($"expected a refusal, got {block.KindName()}");

    private static EvidenceLayer Layer(IReadOnlyList<string> sources) => new()
    {
        Name = "register",
        Sources = sources,
        Requirement = LayerRequirement.Required,
        Role = AnswerRole.Controlling,
    };

    private static QueryIntent Intent() => new() { Question = "what do we know about these vendors" };
}
