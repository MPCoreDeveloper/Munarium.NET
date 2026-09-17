namespace Munarium.Core.Tests.Chronology;

using Munarium.Chronology;

/// <summary>
/// Tests for the two rule families that reason about elapsed time and containment rather than a pair's
/// order.
/// </summary>
public class ChronologyBoundsTests
{
    [Fact]
    public void ADurationRuleReportsThePossibleGap()
    {
        var rules = new ChronologyRules
        {
            Durations = [new DurationRule("start_date", "end_date") with { MaxDays = 10 }],
        };

        var violations = ChronologyEvaluator.Evaluate(
            [
                Event("job", "start_date", "2020-01-01", ChronoOrigin.Ledger),
                Event("job", "end_date", "2020-06-01", ChronoOrigin.Candidate),
            ],
            rules,
            now: null);

        var violation = Assert.Single(violations);
        Assert.Equal(ChronoRuleKind.Duration, violation.Kind);
        Assert.Equal(152, violation.Extras["gap_days_min"]!.GetValue<long>());
        Assert.Equal(152, violation.Extras["gap_days_max"]!.GetValue<long>());
        Assert.Contains("max_days=10", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With two month-precision dates the gap is a range, and a bound is only violated when the
    /// shortest - or the longest - reading violates it.
    /// </summary>
    [Fact]
    public void ADurationRuleReasonsAboutTheWholePossibleGap()
    {
        var rules = new ChronologyRules
        {
            Durations = [new DurationRule("start_date", "end_date") with { MinDays = 90 }],
        };

        // 2020-01 to 2020-03: at most the gap from 1 January to 31 March is 90 days, so it is in bounds.
        Assert.Empty(ChronologyEvaluator.Evaluate(
            [
                Event("phase", "start_date", "2020-01", ChronoOrigin.Ledger),
                Event("phase", "end_date", "2020-03", ChronoOrigin.Candidate),
            ],
            rules,
            now: null));
    }

    [Fact]
    public void AContainmentRuleFiresOnlyWhenTheIntervalIsCertain()
    {
        var rules = new ChronologyRules { Contains = [new ContainsRule("deployment", "release_date")] };

        Assert.Single(ChronologyEvaluator.Evaluate(
            [
                Event("service", "deployment", "March 2020", ChronoOrigin.Ledger),
                Event("service", "release_date", "June 2020", ChronoOrigin.Candidate),
            ],
            rules,
            now: null));

        Assert.Empty(ChronologyEvaluator.Evaluate(
            [
                Event("service", "deployment", "March 2020", ChronoOrigin.Ledger),
                Event("service", "release_date", "circa June 2020", ChronoOrigin.Candidate),
            ],
            rules,
            now: null));
    }

    [Fact]
    public void AnOverlapRuleFiresOnceForASymmetricPair()
    {
        var rules = new ChronologyRules { ForbidOverlap = [new OverlapRule("tour_a", "tour_b")] };

        // Both patterns match both events, so the pair arrives twice and must be reported once.
        var violations = ChronologyEvaluator.Evaluate(
            [
                Event("band", "tour_a", "2020-05-01", ChronoOrigin.Ledger),
                Event("band", "tour_b", "2020-05-01", ChronoOrigin.Candidate),
            ],
            rules,
            now: null);

        Assert.Single(violations);
    }

    [Fact]
    public void AnOverlapRuleIgnoresMonthPrecisionOverlaps()
    {
        var rules = new ChronologyRules { ForbidOverlap = [new OverlapRule("tour_a", "tour_b")] };

        Assert.Empty(ChronologyEvaluator.Evaluate(
            [
                Event("band", "tour_a", "2020-05", ChronoOrigin.Ledger),
                Event("band", "tour_b", "2020-05", ChronoOrigin.Candidate),
            ],
            rules,
            now: null));
    }

    [Fact]
    public void TheEvaluationIsDeterministicWhateverOrderTheEventsArriveIn()
    {
        var rules = new ChronologyRules
        {
            Order = [new OrderRule("birth_date", "death_date")],
            Durations = [new DurationRule("birth_date", "death_date") with { MinDays = 10_000 }],
        };

        ChronoEvent[] timeline =
        [
            Event("hero", "birth_date", "1950", ChronoOrigin.Ledger),
            Event("hero", "death_date", "1940", ChronoOrigin.Candidate),
        ];

        Assert.Equal(
            Describe(ChronologyEvaluator.Evaluate(timeline, rules, null)),
            Describe(ChronologyEvaluator.Evaluate([.. timeline.Reverse()], rules, null)));
    }

    private static string Describe(IReadOnlyList<ChronoViolation> violations) =>
        string.Join('|', violations.Select(violation => $"{violation.Kind}:{violation.Message}"));

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
}
