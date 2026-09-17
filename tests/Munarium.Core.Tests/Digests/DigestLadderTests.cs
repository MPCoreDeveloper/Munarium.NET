namespace Munarium.Core.Tests.Digests;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Digests;
using Munarium.Ledger;

/// <summary>
/// Tests for the digest ladder: the rungs, their content, and the property that makes them usable -
/// rebuilt under a pin, they reproduce what the ledger held then.
/// </summary>
public class DigestLadderTests
{
    [Theory]
    [InlineData("book.ch1", "book")]
    [InlineData("book.ch1.scene2", "book")]
    [InlineData("notes", "notes")]
    [InlineData("", "")]
    public void AGroupIsTheFirstScopeSegment(string scope, string group) =>
        Assert.Equal(group, DigestLadder.GroupOf(scope));

    [Fact]
    public void TheLadderIsDeterministic()
    {
        Assert.Equal(
            Describe(DigestLadder.Build(ClaimFixture.VersionId, Facts())),
            Describe(DigestLadder.Build(ClaimFixture.VersionId, Facts())));
    }

    [Fact]
    public void TheLadderHasAScopeGroupAndRollupRung()
    {
        var ladder = DigestLadder.Build(ClaimFixture.VersionId, Facts());

        Assert.Equal(3, ladder.Count(digest => digest.Tier == 0));
        Assert.Equal(2, ladder.Count(digest => digest.Tier == 1));
        Assert.Equal(1, ladder.Count(digest => digest.Tier == 2));
    }

    /// <summary>
    /// The content is part of the contract rather than a detail: it is what the rung's hash is taken
    /// over, so two builds that formatted it differently would disagree on the pin that produced them.
    /// </summary>
    [Fact]
    public void TheScopeRungIsTheScopesFactsAsCanonicalLines()
    {
        var rung = DigestLadder.Tier0(ClaimFixture.VersionId, Facts())[0];

        Assert.Equal("book.ch1", rung.ScopePath);
        Assert.Equal("[book.ch1] 1 facts\nhero.eyes=green", rung.Content);
        Assert.Equal(new SequenceNumber(1), rung.BuiltFromSequence);
        Assert.Equal(64, rung.ContentHash.Length);
    }

    [Fact]
    public void TheGroupRungElidesValuesAndKeepsSortedKeys()
    {
        var rung = DigestLadder.Tier1(ClaimFixture.VersionId, Facts())[0];

        Assert.Equal("book", rung.ScopePath);
        Assert.Equal("[group book] 2 facts across 2 scopes; keys: hero.eyes, hero.home", rung.Content);
        Assert.DoesNotContain("green", rung.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRollupRungCountsFactsScopesAndSubjects()
    {
        var rung = DigestLadder.Tier2(ClaimFixture.VersionId, Facts());

        Assert.Equal(string.Empty, rung.ScopePath);
        Assert.Equal("[rollup] 3 facts, 3 scopes, 2 subjects: hero, villain", rung.Content);
    }

    [Fact]
    public void FactsWithNoScopeStillGetAScopeRung() =>
        Assert.Contains(
            DigestLadder.Tier0(ClaimFixture.VersionId, [ClaimFixture.Create("d", 1, "hero", "eyes", "green", scope: null)]),
            rung => rung.ScopePath.Length == 0 && rung.Content.Contains('['));

    /// <summary>
    /// The pin property the ladder exists for: rungs rebuilt from the facts that were current then do
    /// not contain what was said later.
    /// </summary>
    [Fact]
    public void RebuildingUnderAPinDiffersFromTheHead()
    {
        Claim[] atHead =
        [
            ClaimFixture.Create("a", 1, "hero", "eyes", "green"),
            ClaimFixture.Create("b", 5, "hero", "home", "harbor"),
        ];

        var head = DigestLadder.Tier0(ClaimFixture.VersionId, atHead)[0];
        var pinned = DigestLadder.Tier0(ClaimFixture.VersionId, [.. atHead.Where(claim => claim.Sequence <= new SequenceNumber(3))])[0];

        Assert.NotEqual(head.ContentHash, pinned.ContentHash);
        Assert.DoesNotContain("harbor", pinned.Content, StringComparison.Ordinal);
    }

    private static Claim[] Facts() =>
    [
        ClaimFixture.Create("a", 1, "hero", "eyes", "green", scope: "book.ch1"),
        ClaimFixture.Create("b", 2, "hero", "home", "harbor", scope: "book.ch2"),
        ClaimFixture.Create("c", 3, "villain", "name", "Mora", scope: "notes"),
    ];

    private static string Describe(IReadOnlyList<Digest> ladder) =>
        string.Join('|', ladder.Select(rung => $"{rung.Tier}:{rung.ScopePath}:{rung.ContentHash}"));
}
