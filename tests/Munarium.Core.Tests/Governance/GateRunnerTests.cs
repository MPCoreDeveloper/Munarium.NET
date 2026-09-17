namespace Munarium.Core.Tests.Governance;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Governance.Gates;

/// <summary>
/// Tests for the runner: the order the gates run in, the dedup between them, and what the accept path
/// reads back out.
/// </summary>
public class GateRunnerTests
{
    /// <summary>
    /// The anchor finding subsumes the ledger conflict for the same detail: one disagreement is
    /// reported once, with the reason that can be acted on.
    /// </summary>
    [Fact]
    public void AnAnchorFindingSubsumesTheLedgerConflictForTheSameDetail()
    {
        var findings = DeterministicGates.Run(
            ClaimFixture.SnapshotWithAnchors(
                [ClaimFixture.Create("c1", 1, "hero", "eyes", "green")],
                ClaimFixture.Lock("hero.eyes", "green")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "eyes", "blue")] });

        var finding = Assert.Single(findings);
        Assert.Equal(AnchorConsistency.RuleId, finding.RuleId);
    }

    /// <summary>
    /// The dedup is per claim key, not per unit: a conflict about a detail that is not locked is still
    /// reported as a conflict.
    /// </summary>
    [Fact]
    public void AConflictAboutAnotherDetailSurvivesTheDedup()
    {
        var findings = DeterministicGates.Run(
            ClaimFixture.SnapshotWithAnchors(
                [
                    ClaimFixture.Create("c1", 1, "hero", "eyes", "green"),
                    ClaimFixture.Create("c2", 2, "hero", "home", "harbor"),
                ],
                ClaimFixture.Lock("hero.eyes", "green")),
            new Candidate
            {
                Claims =
                [
                    ClaimFixture.Propose("hero", "eyes", "blue"),
                    ClaimFixture.Propose("hero", "home", "cove"),
                ],
            });

        Assert.Equal(
            [AnchorConsistency.RuleId, LedgerConflict.RuleId],
            findings.Select(finding => finding.RuleId));
    }

    /// <summary>A unit can break several rules at once, and every one of them is reported.</summary>
    [Fact]
    public void EveryRuleTheCandidateBreaksIsReported()
    {
        var findings = DeterministicGates.Run(
            ClaimFixture.SnapshotWithAnchors(
                [ClaimFixture.Create("c1", 1, "hero", "eyes", "green")],
                ClaimFixture.Lock("hero.eyes", "green")),
            new Candidate
            {
                Text = "As an AI, that request is out of scope.",
                Claims = [ClaimFixture.Propose("hero", "eyes", "blue")],
                Corrections = [ClaimFixture.Propose("villain", "eyes", "red")],
            });

        Assert.Equal(
            [AnchorConsistency.RuleId, OrphanedReference.RuleId, MetaLeakage.RuleId],
            findings.Select(finding => finding.RuleId));
    }

    [Fact]
    public void ACleanCandidateDrawsNoFindings() =>
        Assert.Empty(DeterministicGates.Run(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eyes", "green")),
            new Candidate
            {
                Text = "The harbour was quiet until the second bell.",
                Claims = [ClaimFixture.Propose("hero", "eyes", "hazel", supersedes: "c1")],
            }));

    [Fact]
    public void BackfillDowngradesEveryBlockToAWarning()
    {
        var findings = DeterministicGates.DowngradeBlocks(
            DeterministicGates.Run(
                ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eyes", "green")),
                new Candidate { Claims = [ClaimFixture.Propose("hero", "eyes", "blue")] }));

        Assert.NotEmpty(findings);
        Assert.All(findings, finding => Assert.NotEqual(Severity.Block, finding.Severity));
    }

    /// <summary>
    /// Only a block disputes a claim, and only a finding that names a claim can - a meta-leakage
    /// warning about a unit of text cannot be attributed to a proposal.
    /// </summary>
    [Fact]
    public void OnlyBlockedClaimKeysAreCollected()
    {
        var blocked = DeterministicGates.BlockedClaimKeys(
        [
            .. DeterministicGates.Run(
                ClaimFixture.SnapshotWithAnchors(
                    [ClaimFixture.Create("c1", 1, "hero", "eyes", "green")],
                    ClaimFixture.Lock("hero.eyes", "green")),
                new Candidate
                {
                    Text = "As an AI, I cannot assist.",
                    Claims = [ClaimFixture.Propose("hero", "eyes", "blue")],
                }),
        ]);

        Assert.Equal(["hero.eyes"], blocked);
    }

    [Fact]
    public void AWarnedClaimIsNotDisputed() =>
        Assert.Empty(DeterministicGates.BlockedClaimKeys(
            DeterministicGates.Run(
                ClaimFixture.Snapshot(),
                new Candidate
                {
                    Text = "As an AI, I cannot assist.",
                    Claims = [ClaimFixture.Propose("hero", "eyes", "blue")],
                })));
}
