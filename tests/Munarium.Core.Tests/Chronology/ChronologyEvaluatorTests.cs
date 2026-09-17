namespace Munarium.Core.Tests.Chronology;

using Munarium.Chronology;

/// <summary>
/// Tests for the rule evaluation: which rule fires, on which events, and when it stays silent.
/// </summary>
public class ChronologyEvaluatorTests
{
    [Fact]
    public void AnOrderingRulePairsWithinASubjectAndFiresOnACertainViolation()
    {
        var rules = new ChronologyRules { Order = [new OrderRule("birth_date", "death_date")] };

        var violations = ChronologyEvaluator.Evaluate(
            [
                Event("hero", "birth_date", "1950", ChronoOrigin.Ledger),
                Event("hero", "death_date", "1940", ChronoOrigin.Candidate),
                // A different subject's date must not be paired with hero's: the targets are patterns.
                Event("villain", "death_date", "1930", ChronoOrigin.Ledger),
            ],
            rules,
            now: null);

        var violation = Assert.Single(violations);
        Assert.Equal(ChronoRuleKind.Order, violation.Kind);
        Assert.Equal(["hero.death_date"], violation.CandidateClaims);
        Assert.Equal("death_date", violation.Rule["order"]!["after"]!.GetValue<string>());
    }

    [Fact]
    public void AnAbsoluteTargetPairsAcrossSubjects() =>
        Assert.Single(ChronologyEvaluator.Evaluate(
            [
                Event("draft", "submitted", "1950", ChronoOrigin.Ledger),
                Event("hero", "death_date", "1940", ChronoOrigin.Candidate),
            ],
            new ChronologyRules { Order = [new OrderRule("draft.submitted", "hero.death_date")] },
            now: null));

    [Fact]
    public void AHedgedAssertionFilesNothing() =>
        Assert.Empty(ChronologyEvaluator.Evaluate(
            [
                Event("hero", "birth_date", "circa 1950", ChronoOrigin.Ledger),
                Event("hero", "death_date", "1940", ChronoOrigin.Candidate),
            ],
            new ChronologyRules { Order = [new OrderRule("birth_date", "death_date")] },
            now: null));

    /// <summary>
    /// A rule about two ledger facts is history: nothing is being written, so there is nothing to
    /// dispute and nothing to report.
    /// </summary>
    [Fact]
    public void ARuleBetweenTwoLedgerFactsFilesNothing() =>
        Assert.Empty(ChronologyEvaluator.Evaluate(
            [
                Event("hero", "birth_date", "1950", ChronoOrigin.Ledger),
                Event("hero", "death_date", "1940", ChronoOrigin.Ledger),
            ],
            new ChronologyRules { Order = [new OrderRule("birth_date", "death_date")] },
            now: null));

    [Fact]
    public void ADeadlineRuleFiresOnAnEventThatArrivedLate()
    {
        var rules = new ChronologyRules { Deadlines = [new DeadlineRule("filing", "incident_date", WithinDays: 30)] };

        var violations = ChronologyEvaluator.Evaluate(
            [
                Event("case", "incident_date", "2020-01-01", ChronoOrigin.Ledger),
                Event("case", "filing", "2020-03-01", ChronoOrigin.Candidate),
            ],
            rules,
            now: null);

        var violation = Assert.Single(violations);
        Assert.Equal(ChronoRuleKind.Deadline, violation.Kind);
        Assert.Equal("2020-01-31", violation.Extras["deadline"]!.GetValue<string>());
    }

    /// <summary>
    /// The absence direction is clock-driven: with no event to blame it re-files while the deadline
    /// stays missed, and with no clock it cannot fire at all.
    /// </summary>
    [Fact]
    public void ADeadlineRuleFiresOnAnAbsentEventOnlyWhenTheClockPassedIt()
    {
        var rules = new ChronologyRules { Deadlines = [new DeadlineRule("filing", "incident_date", WithinDays: 30)] };
        ChronoEvent[] timeline = [Event("case", "incident_date", "2020-01-01", ChronoOrigin.Ledger)];

        Assert.Empty(ChronologyEvaluator.Evaluate(timeline, rules, now: null));
        Assert.Empty(ChronologyEvaluator.Evaluate(timeline, rules, now: Day("2020-01-15")));

        var violation = Assert.Single(ChronologyEvaluator.Evaluate(timeline, rules, now: Day("2020-02-15")));
        Assert.Equal("2020-02-15", violation.Extras["now"]!.GetValue<string>());
    }

    private static ChronoEvent Event(string subject, string key, string value, ChronoOrigin origin) => new()
    {
        Subject = subject,
        Key = key,
        Value = value,
        Interval = ChronologyGrammar.Parse(value)
            ?? throw new InvalidOperationException($"The fixture value '{value}' is not a date."),
        ClaimId = origin is ChronoOrigin.Ledger ? $"c-{subject}-{key}" : null,
        Origin = origin,
    };

    private static DateOnly Day(string value) =>
        DateOnly.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
