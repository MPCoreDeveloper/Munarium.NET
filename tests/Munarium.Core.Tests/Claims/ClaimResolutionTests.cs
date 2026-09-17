namespace Munarium.Core.Tests.Claims;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Ledger;

/// <summary>
/// Tests for pin-aware supersession resolution: which claims are current, as of when.
/// </summary>
public class ClaimResolutionTests
{
    [Fact]
    public void ASupersededClaimIsNotCurrentAtTheHead()
    {
        var current = ClaimResolution.Resolve(
            [
                ClaimFixture.Create("c1", 1, "hero", "eyes", "green"),
                ClaimFixture.Create("c2", 2, "hero", "eyes", "blue", supersedes: "c1"),
            ]);

        var claim = Assert.Single(current);
        Assert.Equal("c2", claim.Id);
    }

    /// <summary>
    /// The pin semantic: a claim superseded only after the pin is still current at the pin. The
    /// superseded set is filtered by the pin too, which is what makes this hold.
    /// </summary>
    [Fact]
    public void ASupersessionAfterThePinLeavesTheEarlierValueCurrent()
    {
        var current = ClaimResolution.Resolve(
            [
                ClaimFixture.Create("c1", 1, "hero", "eyes", "green"),
                ClaimFixture.Create("c2", 5, "hero", "eyes", "blue", supersedes: "c1"),
            ],
            new ClaimQuery { AsOfSequence = new SequenceNumber(3) });

        var claim = Assert.Single(current);
        Assert.Equal("c1", claim.Id);
        Assert.Equal("green", claim.Value);
    }

    /// <summary>
    /// A correction that a gate disputed still supersedes: the ledger records that the earlier value
    /// was replaced, which is a fact about the history, whatever the correction's own status became.
    /// </summary>
    [Fact]
    public void ADisputedSupersessionStillSupersedes()
    {
        var current = ClaimResolution.Resolve(
            [
                ClaimFixture.Create("c1", 1, "hero", "eyes", "green"),
                ClaimFixture.Create("c2", 2, "hero", "eyes", "blue", supersedes: "c1", status: ClaimStatus.Disputed),
            ]);

        Assert.Empty(current);
    }

    [Fact]
    public void AScopePrefixMatchesTheScopeAndItsDescendantsOnly()
    {
        var current = ClaimResolution.Resolve(
            [
                ClaimFixture.Create("a", 1, "s", "k1", "v", scope: "book.ch1"),
                ClaimFixture.Create("b", 2, "s", "k2", "v", scope: "book.ch1.scene2"),
                ClaimFixture.Create("c", 3, "s", "k3", "v", scope: "book.ch10"),
            ],
            new ClaimQuery { ScopePrefix = "book.ch1" });

        Assert.Equal(["a", "b"], current.Select(claim => claim.Id));
    }

    [Fact]
    public void AScopePrefixExcludesClaimsWithNoScope()
    {
        var current = ClaimResolution.Resolve(
            [ClaimFixture.Create("a", 1, "s", "k", "v", scope: null)],
            new ClaimQuery { ScopePrefix = "book.ch1" });

        Assert.Empty(current);
    }

    [Fact]
    public void ALimitKeepsTheNewestAndTheOrderStaysAscending()
    {
        var current = ClaimResolution.Resolve(
            [
                .. Enumerable.Range(1, 5).Select(index =>
                    ClaimFixture.Create($"c{index}", index, "s", $"k{index}", "v")),
            ],
            new ClaimQuery { Limit = 2 });

        Assert.Equal([4, 5], current.Select(claim => claim.Sequence.Value));
    }

    [Fact]
    public void ADisputedClaimIsHiddenByDefaultAndVisibleOnRequest()
    {
        var disputed = ClaimFixture.Create("d", 1, "s", "k", "v", status: ClaimStatus.Disputed);

        Assert.Empty(ClaimResolution.Resolve([disputed]));
        Assert.Single(ClaimResolution.Resolve([disputed], new ClaimQuery { Statuses = [ClaimStatus.Disputed] }));
    }

    [Fact]
    public void AResolutionIsDeterministicRegardlessOfInputOrder()
    {
        Claim[] claims =
        [
            ClaimFixture.Create("c2", 2, "hero", "eyes", "blue", supersedes: "c1"),
            ClaimFixture.Create("c1", 1, "hero", "eyes", "green"),
            ClaimFixture.Create("c3", 3, "hero", "home", "harbor"),
        ];

        Assert.Equal(
            ClaimResolution.Resolve(claims).Select(claim => claim.Id),
            ClaimResolution.Resolve([.. claims.Reverse()]).Select(claim => claim.Id));
    }

    [Fact]
    public void ANegativeLimitIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClaimResolution.Resolve([], new ClaimQuery { Limit = -1 }));
}
