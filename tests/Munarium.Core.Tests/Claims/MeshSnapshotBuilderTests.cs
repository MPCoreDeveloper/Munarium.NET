namespace Munarium.Core.Tests.Claims;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Facts;
using Munarium.Ledger;

/// <summary>
/// Tests for the pinned view: what it carries, what a scope and a limit select, and the rungs it rebuilds.
/// </summary>
public class MeshSnapshotBuilderTests
{
    [Fact]
    public async Task TheSnapshotCarriesTheRungsThePinnedFactsProduce()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v1", scope: "notes")]);
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "owner_team", "platform", scope: "notes")]);

        var builder = new MeshSnapshotBuilder(storage);
        var head = await builder.BuildAsync("release-1");
        var pinned = await builder.BuildAsync("release-1", new SequenceNumber(1));

        var headRung = Assert.Single(head.Digests, rung => rung.Tier == 0);
        var pinnedRung = Assert.Single(pinned.Digests, rung => rung.Tier == 0);

        Assert.Equal("notes", headRung.ScopePath);
        Assert.Contains("owner_team", headRung.Content, StringComparison.Ordinal);

        // Rebuilt, not read: at the earlier pin the rung is a function of the one fact that existed then.
        Assert.DoesNotContain("owner_team", pinnedRung.Content, StringComparison.Ordinal);
        Assert.NotEqual(headRung.ContentHash, pinnedRung.ContentHash);
    }

    [Fact]
    public async Task TheLadderRungIsPresentEvenWithNoFacts()
    {
        var (_, storage, _) = CandidateFixture.Ledger();

        var snapshot = await new MeshSnapshotBuilder(storage).BuildAsync("release-1");

        Assert.Empty(snapshot.Facts);
        var rollup = Assert.Single(snapshot.Digests);
        Assert.Equal(2, rollup.Tier);
        Assert.Equal("[rollup] 0 facts, 0 scopes, 0 subjects: ", rollup.Content);
    }

    [Fact]
    public async Task AScopePrefixSelectsTheScopeAndItsDescendants()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v1", scope: "notes.release")]);
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "owner_team", "platform", scope: "ops")]);
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "tier", "gold")]);

        var builder = new MeshSnapshotBuilder(storage);

        Assert.Equal(3, (await builder.BuildAsync("release-1")).Facts.Count);
        Assert.Equal(
            ["service.api_version"],
            (await builder.BuildAsync("release-1", scopePrefix: "notes")).Facts.Select(claim => claim.ClaimKey));

        // A claim with no scope is not in any named scope.
        Assert.DoesNotContain(
            (await builder.BuildAsync("release-1", scopePrefix: "ops")).Facts,
            claim => claim.Subject != "service" || claim.Key != "owner_team");
    }

    [Fact]
    public async Task AFactLimitKeepsTheNewestFacts()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v1")]);
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "owner_team", "platform")]);
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "tier", "gold")]);

        var snapshot = await new MeshSnapshotBuilder(storage).BuildAsync("release-1", factLimit: 2);

        Assert.Equal([2, 3], snapshot.Facts.Select(claim => claim.Sequence.Value));

        // The ladder counts what the snapshot holds, which is what makes it a rung of that view and not of
        // the whole ledger.
        Assert.Contains(
            "[rollup] 2 facts",
            snapshot.Digests.Single(rung => rung.Tier == 2).Content,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A limit counts current facts, not rows: a superseded fact must not occupy a slot, or the reader would
    /// get fewer facts than it asked for.
    /// </summary>
    [Fact]
    public async Task ALimitCountsCurrentFactsOnly()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v1")]);
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v2", ClaimType.Correction)]);

        var snapshot = await new MeshSnapshotBuilder(storage).BuildAsync("release-1", factLimit: 1);

        Assert.Equal("v2", Assert.Single(snapshot.Facts).Value);
    }
}
