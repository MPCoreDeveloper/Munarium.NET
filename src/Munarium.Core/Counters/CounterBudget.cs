namespace Munarium.Counters;

using System.Globalization;
using System.Text.Json.Nodes;
using Munarium.Claims;

/// <summary>
/// Whole-document frequency budgets: how often a pattern may be used across a work, and what to tell
/// the writer as it approaches the ceiling.
/// </summary>
/// <remarks>
/// The budget is absolute rather than per unit, because repetition is a property of the whole
/// document: a rule that only counted within the current unit would be satisfied by spreading the same
/// twelve uses across twelve units.
/// <para>
/// Counting and judging are separate so that a counter can be kept before a budget exists - the total
/// is then already right when somebody decides the ceiling.
/// </para>
/// </remarks>
public static class CounterBudget
{
    /// <summary>The dotted rule identifier recorded with a finding from this check.</summary>
    public const string RuleId = "gate.counter-budget";

    /// <summary>
    /// Counts non-overlapping occurrences of a pattern, case-insensitively.
    /// </summary>
    /// <remarks>
    /// Non-overlapping is the whole definition: occurrences are taken left to right without reusing
    /// characters, so <c>aaa</c> in <c>aaaaa</c> counts once. The alternative - sliding by one - is a
    /// different measure, and a budget written against one of them would be wrong by the other.
    /// </remarks>
    /// <param name="text">The text to count in.</param>
    /// <param name="pattern">The pattern to count; an empty pattern counts nothing.</param>
    /// <returns>The number of occurrences.</returns>
    public static int Count(string text, string pattern)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0)
        {
            return 0;
        }

        var haystack = text.ToLowerInvariant();
        var needle = pattern.ToLowerInvariant();
        var count = 0;

        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal);
            index >= 0;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Reports the counters that are over their budget.
    /// </summary>
    /// <param name="totals">The counters to check.</param>
    /// <param name="scopePath">The scope the check ran for, recorded on the finding.</param>
    /// <returns>One warning finding per exhausted budget.</returns>
    public static IReadOnlyList<GateFinding> Findings(
        IReadOnlyList<CounterTotal> totals,
        string? scopePath)
    {
        ArgumentNullException.ThrowIfNull(totals);

        var findings = new List<GateFinding>();

        foreach (var total in totals.Where(total => total.IsOverBudget))
        {
            findings.Add(new GateFinding
            {
                RuleId = RuleId,
                Severity = Severity.Warn,
                Message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"counter '{total.Key}' at {total.Total} exceeds whole-document budget {total.Budget.GetValueOrDefault()}"),
                ScopePath = scopePath,
                Detail = new JsonObject
                {
                    ["counter_key"] = total.Key,
                    ["total"] = total.Total,
                    ["budget"] = total.Budget,
                },
            });
        }

        return findings;
    }

    /// <summary>
    /// Writes the directives a writer is given about its counters.
    /// </summary>
    /// <remarks>
    /// A budget the writer is not told about is only a way to fail it, so the usage line travels with
    /// the work, and an exhausted budget becomes an instruction not to use the pattern at all. One
    /// use remaining is called out separately: the difference between "you have room" and "this is the
    /// last one" is the difference between a usable directive and a surprise.
    /// </remarks>
    /// <param name="totals">The counters to describe.</param>
    /// <returns>The directives, one per line; empty when no counter has a budget.</returns>
    public static string Directives(IReadOnlyList<CounterTotal> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);

        var lines = new List<string>();

        foreach (var total in totals.Where(total => total.Budget is not null))
        {
            var budget = total.Budget.GetValueOrDefault();
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{total.Key}: used {total.Total}/{budget}"));

            if (total.Total >= budget)
            {
                lines.Add($"AVOID: '{total.Key}' — budget exhausted");
            }
            else if (total.Total + 1 == budget)
            {
                lines.Add($"CAUTION: '{total.Key}' — one use remaining");
            }
        }

        return string.Join('\n', lines);
    }
}
