namespace Munarium.Core.Tests.Governance;

using Munarium.Chronology;
using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Governance.Gates;
using Munarium.Ledger;

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

    /// <summary>
    /// The absence check reads the snapshot's own instant, because the snapshot carries it: the same
    /// snapshot therefore produces the same finding for anyone who holds it, with no clock read and no date
    /// passed in.
    /// </summary>
    [Fact]
    public void TheAbsenceCheckReadsTheSnapshotsOwnInstant()
    {
        var rules = new ChronologyRules
        {
            Deadlines = [new DeadlineRule("case.filing", "case.incident_date", WithinDays: 30)],
        };

        var snapshot = ClaimFixture.Snapshot(ClaimFixture.Create(
            LedgerIds.NewAt(Instant(2020, 3, 15)),
            1,
            "case",
            "incident_date",
            "2020-01-01"));

        // Nothing arrived, and the deadline (2020-01-31) is behind the snapshot's own instant.
        var findings = ChronologyGate.Evaluate(snapshot, new Candidate(), rules);

        var finding = Assert.Single(findings);
        Assert.Equal(ChronologyGate.DeadlineRuleId, finding.RuleId);
        Assert.Equal("2020-01-31", finding.Detail!["deadline"]!.GetValue<string>());
        Assert.Equal("2020-03-15", finding.Detail["now"]!.GetValue<string>());

        // The same call twice is the same answer: nothing here reads a wall clock.
        Assert.Equal(
            Describe(findings),
            Describe(ChronologyGate.Evaluate(snapshot, new Candidate(), rules)));
    }

    [Fact]
    public void TheAbsenceCheckIsSilentBeforeTheDeadlineOrWhenDisabled()
    {
        var rules = new ChronologyRules
        {
            Deadlines = [new DeadlineRule("case.filing", "case.incident_date", WithinDays: 30)],
        };

        var early = ClaimFixture.Snapshot(ClaimFixture.Create(
            LedgerIds.NewAt(Instant(2020, 1, 15)),
            1,
            "case",
            "incident_date",
            "2020-01-01"));

        Assert.Empty(ChronologyGate.Evaluate(early, new Candidate(), rules));

        // An explicit null says "do not run the absence check", rather than being the default.
        var late = ClaimFixture.Snapshot(ClaimFixture.Create(
            LedgerIds.NewAt(Instant(2020, 3, 15)),
            1,
            "case",
            "incident_date",
            "2020-01-01"));

        Assert.Empty(ChronologyGate.Evaluate(late, new Candidate(), rules, now: null));
    }

    [Fact]
    public void ASnapshotOfOpaqueIdentitiesHasNoInstantToRead()
    {
        var rules = new ChronologyRules
        {
            Deadlines = [new DeadlineRule("case.filing", "case.incident_date", WithinDays: 30)],
        };

        Assert.Null(ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "case", "incident_date", "2020-01-01")).WrittenOn);
        Assert.Empty(ChronologyGate.Evaluate(
            ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "case", "incident_date", "2020-01-01")),
            new Candidate(),
            rules));
    }

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

    private static DateTimeOffset Instant(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static string Describe(IReadOnlyList<GateFinding> findings) =>
        string.Join('|', findings.Select(finding => $"{finding.RuleId}:{finding.Message}"));
}
