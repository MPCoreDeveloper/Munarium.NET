namespace Munarium.Core.Tests.Counters;

using Munarium.Claims;
using Munarium.Counters;

/// <summary>
/// Tests for whole-document frequency budgets: the count, the ceiling, and the directive a writer is
/// given.
/// </summary>
public class CounterBudgetTests
{
    [Fact]
    public void ACountIsCaseInsensitive() =>
        Assert.Equal(2, CounterBudget.Count("The Bell rang. the bell again.", "the bell"));

    [Fact]
    public void AnEmptyPatternCountsNothing() =>
        Assert.Equal(0, CounterBudget.Count("the bell rang", string.Empty));

    /// <summary>
    /// Occurrences are taken left to right without reusing characters, so a pattern cannot be counted
    /// twice for one stretch of text.
    /// </summary>
    [Fact]
    public void OccurrencesDoNotOverlap() =>
        Assert.Equal(1, CounterBudget.Count("aaaaa", "aaa"));

    [Fact]
    public void OnlyTheExhaustedBudgetsAreReported()
    {
        var findings = CounterBudget.Findings(Totals(), "ch4");

        var finding = Assert.Single(findings);
        Assert.Equal(CounterBudget.RuleId, finding.RuleId);
        Assert.Equal(Severity.Warn, finding.Severity);
        Assert.Equal("flashback", finding.Detail!["counter_key"]!.GetValue<string>());
    }

    /// <summary>
    /// An unbudgeted counter is counted but never reported: the total is kept so that a ceiling added
    /// later is measured against a history rather than from that point on.
    /// </summary>
    [Fact]
    public void AnUnbudgetedCounterIsCountedButNotReported() =>
        Assert.DoesNotContain(
            CounterBudget.Findings(Totals(), null),
            finding => finding.Detail!["counter_key"]!.GetValue<string>() == "unbudgeted");

    [Fact]
    public void TheDirectivesSayWhatIsExhaustedAndWhatIsNearlyExhausted()
    {
        var directives = CounterBudget.Directives(Totals());

        Assert.Contains("AVOID: 'flashback' — budget exhausted", directives, StringComparison.Ordinal);
        Assert.Contains("CAUTION: 'storm' — one use remaining", directives, StringComparison.Ordinal);
        Assert.DoesNotContain("unbudgeted", directives, StringComparison.Ordinal);
    }

    [Fact]
    public void NoBudgetMeansNoDirectives() =>
        Assert.Equal(
            string.Empty,
            CounterBudget.Directives([new CounterTotal { Key = "pattern", Total = 3 }]));

    private static IReadOnlyList<CounterTotal> Totals() =>
    [
        new CounterTotal { Key = "flashback", Total = 3, Budget = 2 },
        new CounterTotal { Key = "storm", Total = 1, Budget = 2 },
        new CounterTotal { Key = "unbudgeted", Total = 9 },
    ];
}
