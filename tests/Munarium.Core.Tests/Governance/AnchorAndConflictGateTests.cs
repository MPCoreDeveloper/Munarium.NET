namespace Munarium.Core.Tests.Governance;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Governance.Gates;

/// <summary>
/// Tests for the three gates that judge proposals against the ledger: anchors, conflicts and the
/// references a correction makes.
/// </summary>
public class AnchorAndConflictGateTests
{
    [Fact]
    public void AClaimThatContradictsALockedAnchorIsBlocked()
    {
        var findings = AnchorConsistency.Evaluate(
            SnapshotWithLock("hero.eyes", "green"),
            new Candidate { ScopePath = "ch2", Claims = [ClaimFixture.Propose("hero", "eyes", "blue")] });

        var finding = Assert.Single(findings);
        Assert.Equal(AnchorConsistency.RuleId, finding.RuleId);
        Assert.Equal(Severity.Block, finding.Severity);
        Assert.Equal("hero.eyes", finding.ClaimKey);
        Assert.Equal("green", finding.Detail!["locked_value"]!.GetValue<string>());
    }

    /// <summary>
    /// A correction is judged against a lock as well: declaring that a value is being corrected does
    /// not unlock it.
    /// </summary>
    [Fact]
    public void ACorrectionCannotOverruleALockedAnchor() =>
        Assert.Single(AnchorConsistency.Evaluate(
            SnapshotWithLock("hero.birth_date", "1930"),
            new Candidate { Corrections = [ClaimFixture.Propose("hero", "birth_date", "1925")] }));

    [Theory]
    [InlineData("green")]
    [InlineData("Green")]
    [InlineData(" green ")]
    public void AClaimThatAgreesWithTheAnchorIsPermitted(string value) =>
        Assert.Empty(AnchorConsistency.Evaluate(
            SnapshotWithLock("hero.eyes", "green"),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "eyes", value)] }));

    [Fact]
    public void AClaimThatContradictsTheAcceptedCanonIsBlocked()
    {
        var findings = LedgerConflict.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eyes", "green")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "eyes", "blue")] });

        var finding = Assert.Single(findings);
        Assert.Equal(LedgerConflict.RuleId, finding.RuleId);
        Assert.Equal(Severity.Block, finding.Severity);
        Assert.Equal("c1", finding.Detail!["canon_claim_id"]!.GetValue<string>());
        Assert.Equal(1, finding.Detail["canon_seq"]!.GetValue<long>());
    }

    /// <summary>
    /// A claim that names what it supersedes is declaring a change, so it is exempt - that is the
    /// difference between a correction and a silent overwrite.
    /// </summary>
    [Fact]
    public void ADeclaredSupersessionIsExempt() =>
        Assert.Empty(LedgerConflict.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eyes", "green")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "eyes", "blue", supersedes: "c1")] }));

    [Fact]
    public void AClaimThatRepeatsTheCanonIsNoConflict() =>
        Assert.Empty(LedgerConflict.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eyes", "green")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "eyes", "GREEN")] }));

    [Fact]
    public void TheCanonIsTheNewestFactForTheKey()
    {
        var findings = LedgerConflict.Evaluate(
            ClaimFixture.Snapshot(
                ClaimFixture.Create("c1", 1, "hero", "eyes", "green"),
                ClaimFixture.Create("c2", 5, "hero", "eyes", "blue", supersedes: "c1")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "eyes", "hazel")] });

        Assert.Equal("c2", Assert.Single(findings).Detail!["canon_claim_id"]!.GetValue<string>());
    }

    [Fact]
    public void ACorrectionOfSomethingNeverEstablishedIsOrphaned()
    {
        var findings = OrphanedReference.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eyes", "green")),
            new Candidate { Corrections = [ClaimFixture.Propose("villain", "eyes", "red")] });

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanedReference.RuleId, finding.RuleId);
        Assert.Equal(Severity.Warn, finding.Severity);
        Assert.Equal("villain.eyes", finding.ClaimKey);
    }

    /// <summary>A known subject with a detail it never had is the same mistake one level down.</summary>
    [Fact]
    public void ACorrectionOfAnUnestablishedDetailIsOrphaned() =>
        Assert.Single(OrphanedReference.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eyes", "green")),
            new Candidate { Corrections = [ClaimFixture.Propose("hero", "home", "harbor")] }));

    [Fact]
    public void ACorrectionOfAnEstablishedDetailIsNotOrphaned() =>
        Assert.Empty(OrphanedReference.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eyes", "green")),
            new Candidate { Corrections = [ClaimFixture.Propose("hero", "eyes", "blue")] }));

    private static MeshSnapshot SnapshotWithLock(string detailKey, string lockedValue) =>
        ClaimFixture.SnapshotWithAnchors([], ClaimFixture.Lock(detailKey, lockedValue));
}
