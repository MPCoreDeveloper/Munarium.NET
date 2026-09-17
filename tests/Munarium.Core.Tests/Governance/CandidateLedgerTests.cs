namespace Munarium.Core.Tests.Governance;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Governance.Gates;
using Munarium.Ledger;

/// <summary>
/// Tests for the ledger's write path for a candidate: one unit, judged as a whole, with a blocked claim
/// recorded as disputed rather than dropped.
/// </summary>
public class CandidateLedgerTests
{
    [Fact]
    public async Task AClaimTheGatesDoNotBlockLandsAsAStoredFact()
    {
        var (ledger, storage, facts) = CandidateFixture.Ledger();

        var outcome = await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v2")]);

        var recorded = Recorded(outcome);
        var claim = Assert.Single(recorded.Claims);
        Assert.Equal("service.api_version", claim.ClaimKey);
        Assert.Equal("v2", claim.Value);
        Assert.Equal(ClaimStatus.Accepted, claim.Status);
        Assert.Equal(new SequenceNumber(1), recorded.Head);
        Assert.Empty(recorded.Findings);

        // The stored fact is what a snapshot re-reads, with the triple and the claim key as its lineage.
        var stored = Assert.Single((await facts.SliceAsync(await facts.CurrentPinAsync(), "release-1")).Facts).Fact;
        Assert.Equal("service", stored.Subject);
        Assert.Equal("api_version", stored.Key);
        Assert.Equal("service.api_version", stored.Lineage);
        Assert.Equal(1, storage.AppendCalls);
    }

    /// <summary>
    /// A blocked claim is recorded, not dropped, and only the claim the block names is disputed: the rest
    /// of the batch lands accepted.
    /// </summary>
    [Fact]
    public async Task ABlockedClaimIsRecordedAsDisputedAndItsNeighboursAreNot()
    {
        var (ledger, _, facts) = CandidateFixture.Ledger();
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v1")]);

        var outcome = await ledger.AppendAsync(
            "release-1",
            [
                CandidateFixture.Proposal("service", "api_version", "v2"),
                CandidateFixture.Proposal("service", "owner_team", "platform"),
            ]);

        var recorded = Recorded(outcome);
        Assert.Equal(2, recorded.Claims.Count);
        Assert.Equal(
            "service.api_version",
            recorded.Claims.Single(claim => claim.Status is ClaimStatus.Disputed).ClaimKey);
        Assert.Equal(
            "service.owner_team",
            recorded.Claims.Single(claim => claim.Status is ClaimStatus.Accepted).ClaimKey);

        var finding = Assert.Single(recorded.Findings);
        Assert.Equal(Severity.Block, finding.Severity);
        Assert.Equal(LedgerConflict.RuleId, finding.RuleId);

        var stored = (await facts.SliceAsync(await facts.CurrentPinAsync(), "release-1")).Facts
            .Select(sliced => sliced.Fact)
            .ToDictionary(fact => fact.ClaimKey, StringComparer.Ordinal);

        Assert.Equal(2, stored.Count);
        Assert.Equal(LedgerConflict.RuleId, stored["service.api_version"].Gate);
        Assert.False(string.IsNullOrEmpty(stored["service.api_version"].Reason));
        Assert.Equal(string.Empty, stored["service.owner_team"].Gate);
    }

    /// <summary>
    /// A correction is judged in the corrections plane, so one of something the ledger never held is
    /// surfaced as orphaned - and it still lands, because a repair must be recordable.
    /// </summary>
    [Fact]
    public async Task ACorrectionOfSomethingNeverEstablishedIsSurfacedAndStillLands()
    {
        var (ledger, _, facts) = CandidateFixture.Ledger();

        var outcome = await ledger.AppendAsync(
            "release-1",
            [CandidateFixture.Proposal("service", "api_version", "v2", ClaimType.Correction)]);

        var recorded = Recorded(outcome);
        Assert.Equal(OrphanedReference.RuleId, Assert.Single(recorded.Findings).RuleId);
        Assert.Equal(ClaimStatus.Accepted, Assert.Single(recorded.Claims).Status);
        Assert.Single((await facts.SliceAsync(await facts.CurrentPinAsync(), "release-1")).Facts);
    }

    /// <summary>A candidate that produced only text has findings and nothing to append.</summary>
    [Fact]
    public async Task ATextOnlyCandidateRecordsItsFindingsAndAppendsNothing()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();

        var outcome = await ledger.AppendAsync("release-1", [], "As an AI, I cannot assist with that request.");

        var recorded = Recorded(outcome);
        Assert.Empty(recorded.Claims);
        Assert.Equal(2, recorded.Findings.Count);
        Assert.Equal(SequenceNumber.Zero, recorded.Head);
        Assert.Equal(0, storage.AppendCalls);
    }

    [Fact]
    public async Task ABatchLandsUnderOneConditionalAppend()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();

        await ledger.AppendAsync(
            "release-1",
            [
                CandidateFixture.Proposal("service", "api_version", "v2"),
                CandidateFixture.Proposal("service", "owner_team", "platform"),
            ]);

        Assert.Equal(1, storage.AppendCalls);
    }

    [Fact]
    public async Task ACandidateWithNeitherClaimNorTextIsRefused() =>
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CandidateFixture.Ledger().Ledger.AppendAsync("release-1", []));

    private static CandidateRecorded Recorded(CandidateOutcome outcome) =>
        outcome is CandidateRecorded recorded
            ? recorded
            : throw new InvalidOperationException("The batch did not land.");
}
