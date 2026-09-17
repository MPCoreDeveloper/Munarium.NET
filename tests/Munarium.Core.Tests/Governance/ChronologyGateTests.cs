namespace Munarium.Core.Tests.Governance;

using Munarium.Chronology;
using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Governance.Gates;

/// <summary>
/// Tests for the chronology gate: how the timeline is assembled from the snapshot and the candidate,
/// and how a violation becomes a finding.
/// </summary>
public class ChronologyGateTests
{
    [Fact]
    public void AnOrderingViolationBecomesAFindingThatNamesTheCandidateClaim()
    {
        var findings = ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "birth_date", "1950")),
            new Candidate
            {
                ScopePath = "ch2",
                Claims = [ClaimFixture.Propose("hero", "death_date", "1940")],
            },
            Rules());

        var finding = Assert.Single(findings);
        Assert.Equal(ChronologyGate.OrderRuleId, finding.RuleId);
        Assert.Equal(Severity.Warn, finding.Severity);
        Assert.Equal("ch2", finding.ScopePath);
        Assert.Equal("hero.death_date", finding.ClaimKey);
        Assert.Equal(2, finding.Detail!["chain"]!.AsArray().Count);
        Assert.Equal("order", finding.Detail["kind"]!.GetValue<string>());
    }

    [Fact]
    public void AHedgedCandidateDateFilesNothing() =>
        Assert.Empty(ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "birth_date", "1950")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "death_date", "circa 1940")] },
            Rules()));

    /// <summary>
    /// The correction overlays the ledger fact in the same evaluation: fixing the birth date clears the
    /// violation without a second write.
    /// </summary>
    [Fact]
    public void ACorrectionThatFixesTheLedgerDateClearsTheViolation() =>
        Assert.Empty(ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "birth_date", "1950")),
            new Candidate
            {
                Claims = [ClaimFixture.Propose("hero", "death_date", "1940")],
                Corrections = [ClaimFixture.Propose("hero", "birth_date", "1930", supersedes: "c1")],
            },
            Rules()));

    /// <summary>
    /// The latest assertion replaces the earlier one, and an unparseable value removes the key rather
    /// than leaving the previous value to be judged in its place.
    /// </summary>
    [Fact]
    public void AnUnparseableLatestValueDropsTheKey() =>
        Assert.Empty(ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "birth_date", "1950")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "death_date", "sometime later")] },
            Rules()));

    [Fact]
    public void ARuleDeclaringBlockEscalatesTheSeverity()
    {
        var findings = ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "birth_date", "1950")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "death_date", "1940")] },
            new ChronologyRules
            {
                Order = [new OrderRule("birth_date", "death_date") { Severity = ChronologySeverity.Block }],
            });

        Assert.Equal(Severity.Block, Assert.Single(findings).Severity);
        Assert.Equal(["hero.death_date"], DeterministicGates.BlockedClaimKeys(findings));
    }

    /// <summary>
    /// A claim whose key is neither temporal nor named by a rule never joins the timeline, so a
    /// deployment's chronology rules cannot start judging unrelated properties.
    /// </summary>
    [Fact]
    public void AClaimThatIsNotTemporalNeverJoinsTheTimeline() =>
        Assert.Empty(ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eye_color", "green")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "eye_color", "blue")] },
            Rules()));

    [Fact]
    public void ADeadlineViolationNamesTheDeadlineRuleId()
    {
        // The deadline target is absolute, so the claim it names joins the timeline even though its key
        // carries no temporal pattern.
        var findings = ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "case", "incident_date", "2020-01-01")),
            new Candidate { Claims = [ClaimFixture.Propose("case", "filing", "2020-03-01")] },
            new ChronologyRules
            {
                Deadlines = [new DeadlineRule("case.filing", "case.incident_date", WithinDays: 30)],
            });

        Assert.Equal(ChronologyGate.DeadlineRuleId, Assert.Single(findings).RuleId);
    }

    /// <summary>
    /// A deadline whose arrival key is neither temporal nor absolute never joins the timeline, so the
    /// rule cannot see the event it is about and files nothing.
    /// </summary>
    [Fact]
    public void ADeadlineOverAKeyThatNeverJoinsTheTimelineFilesNothing() =>
        Assert.Empty(ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "case", "incident_date", "2020-01-01")),
            new Candidate { Claims = [ClaimFixture.Propose("case", "filing", "2020-03-01")] },
            new ChronologyRules
            {
                Deadlines = [new DeadlineRule("filing", "incident_date", WithinDays: 30)],
            }));

    /// <summary>No rules means the gate is not armed, which is the whole family's contract.</summary>
    [Fact]
    public void AnUndeclaredRuleSetProducesNothing() =>
        Assert.Empty(ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "birth_date", "1950")),
            new Candidate { Claims = [ClaimFixture.Propose("hero", "death_date", "1940")] },
            new ChronologyRules()));

    [Theory]
    [InlineData("*_date", "birth_date", true)]
    [InlineData("*_date", "date_of_birth", false)]
    [InlineData("date_*", "date_of_birth", true)]
    [InlineData("*_due", "amount_due", true)]
    [InlineData("due", "amount_due", false)]
    [InlineData("*_when", "when", false)]
    public void TheTemporalKeyPatternsMatchInFull(string pattern, string key, bool matches) =>
        Assert.Equal(matches, ChronologyPattern.KeyPatternMatches(pattern, key));

    private static ChronologyRules Rules() => new()
    {
        Order = [new OrderRule("birth_date", "death_date")],
    };
}
