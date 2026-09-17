namespace Munarium.Core.Tests.Governance;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Governance;
using Munarium.Governance.Gates;
using Munarium.Ledger;

/// <summary>
/// Tests for reading a version's findings back out of its stream, and for the property that makes the port's
/// mechanism stronger than the original's: the verdict travels in the same append as the claims it judges.
/// </summary>
public class FindingsLedgerTests
{
    [Fact]
    public async Task AVerdictIsRecordedInTheSameAppendAsTheClaimItJudges()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v1")]);

        var outcome = await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v2")]);

        var recorded = Recorded(outcome);
        Assert.Equal(2, storage.AppendCalls);

        // One append for the second write: its claim and its findings landed together.
        Assert.Equal(new SequenceNumber(3), recorded.Head);
        Assert.Equal(new SequenceNumber(3), recorded.FindingsSequence);

        // The claim the gates judged sits at 2 and its verdict at 3, in that order.
        Assert.Equal(new SequenceNumber(2), Assert.Single(recorded.Claims).Sequence);

        var stored = Assert.Single(await new FindingsLedger(storage).ReadAsync("release-1"));
        Assert.Equal(new SequenceNumber(3), stored.Sequence);
        Assert.Equal(LedgerConflict.RuleId, stored.Finding.RuleId);
        Assert.Equal("service.api_version", stored.Finding.ClaimKey);
    }

    /// <summary>
    /// A write that produced no findings records no event, so a clean stream stays a stream of claims.
    /// </summary>
    [Fact]
    public async Task ACleanWriteRecordsNoFindingsEvent()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();

        var recorded = Recorded(await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v1")]));

        Assert.Empty(recorded.Findings);
        Assert.Null(recorded.FindingsSequence);
        Assert.Equal(new SequenceNumber(1), recorded.Head);
        Assert.Empty(await new FindingsLedger(storage).ReadAsync("release-1"));
    }

    /// <summary>
    /// A candidate that produced only text has nothing to claim, but its findings are still a record of what
    /// was decided - so they are recorded, and the head moves for them.
    /// </summary>
    [Fact]
    public async Task ATextOnlyCandidateStillRecordsItsVerdict()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();

        var recorded = Recorded(await ledger.AppendAsync("release-1", [], "As an AI, I cannot assist."));

        Assert.Empty(recorded.Claims);
        Assert.Equal(new SequenceNumber(1), recorded.Head);
        Assert.Equal(new SequenceNumber(1), recorded.FindingsSequence);
        Assert.Equal(2, (await new FindingsLedger(storage).ReadAsync("release-1")).Count);
    }

    /// <summary>
    /// A pin sees the findings recorded up to it: a reader standing before the verdict does not read it.
    /// </summary>
    [Fact]
    public async Task APinSeesOnlyTheFindingsRecordedUpToIt()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v1")]);
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v2")]);

        var findings = new FindingsLedger(storage);

        Assert.Single(await findings.ReadAsync("release-1"));
        Assert.Empty(await findings.ReadAsync("release-1", new FindingsQuery { AsOfSequence = new SequenceNumber(2) }));
    }

    [Fact]
    public async Task AQuerySelectsByRuleSeverityAndLimit()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v1")]);
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v2")]);
        await ledger.AppendAsync("release-1", [], "Lorem ipsum dolor sit amet");

        var findings = new FindingsLedger(storage);

        Assert.Equal(
            ["gate.ledger-conflict"],
            (await findings.ReadAsync("release-1", new FindingsQuery { Severity = Severity.Block }))
                .Select(stored => stored.Finding.RuleId));

        Assert.Equal(
            ["gate.meta-leakage"],
            (await findings.ReadAsync("release-1", new FindingsQuery { RulePrefix = "gate.meta" }))
                .Select(stored => stored.Finding.RuleId));

        Assert.Equal(
            ["gate.ledger-conflict", "gate.meta-leakage"],
            (await findings.ReadAsync("release-1", new FindingsQuery { RulePrefix = "gate." }))
                .Select(stored => stored.Finding.RuleId));

        Assert.Single(await findings.ReadAsync("release-1", new FindingsQuery { Limit = 1 }));
        Assert.Empty(await findings.ReadAsync("release-1", new FindingsQuery { RuleId = "gate.chronology-order" }));

        // An exact rule and a prefix that cannot both hold select nothing, which is what AND means.
        Assert.Empty(await findings.ReadAsync(
            "release-1",
            new FindingsQuery { RuleId = "gate.ledger-conflict", RulePrefix = "matrix." }));
    }

    [Fact]
    public async Task AnotherVersionsFindingsAreNotVisible() =>
        Assert.Empty(await new FindingsLedger(CandidateFixture.Ledger().Storage)
            .ReadAsync("release-2"));

    private static CandidateRecorded Recorded(CandidateOutcome outcome) =>
        outcome is CandidateRecorded recorded
            ? recorded
            : throw new InvalidOperationException("The batch did not land.");
}
