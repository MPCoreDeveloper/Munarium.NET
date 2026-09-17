namespace Munarium.Chronology;

using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// The elapsed-time half of the chronology evaluation.
/// </summary>
/// <remarks>
/// Both sides may be intervals rather than days, so a duration rule reasons about a range of possible
/// gaps: the shortest reading takes the latest start and the earliest end, the longest the opposite. A
/// bound is only violated when it is violated by the <em>shortest</em> or the <em>longest</em> reading -
/// which is what makes "between 10 and 20 days" meaningful for two month-precision dates that could sit
/// anywhere inside their months.
/// </remarks>
public static partial class ChronologyEvaluator
{
    private static void EvaluateDurations(
        List<ChronoEvent> timeline,
        ChronologyRules rules,
        List<ChronoViolation> violations)
    {
        foreach (var rule in rules.Durations)
        {
            var ruleDetail = new JsonObject
            {
                ["duration"] = new JsonObject
                {
                    ["from"] = rule.Start,
                    ["to"] = rule.End,
                    ["min_days"] = rule.MinDays,
                    ["max_days"] = rule.MaxDays,
                },
            };

            foreach (var (from, to) in Pairs(timeline, rule.Start, rule.End))
            {
                if (!InvolvesCandidate(from, to) || from.Interval.Uncertain || to.Interval.Uncertain)
                {
                    continue;
                }

                var gapMinimum = (long)to.Interval.Start.DayNumber - from.Interval.End.DayNumber;
                var gapMaximum = (long)to.Interval.End.DayNumber - from.Interval.Start.DayNumber;

                string? problem = null;
                if (rule.MaxDays is { } maximum && gapMinimum > maximum)
                {
                    problem = string.Create(CultureInfo.InvariantCulture, $"definitely exceeds max_days={maximum}");
                }
                else if (rule.MinDays is { } minimum && gapMaximum < minimum)
                {
                    problem = string.Create(CultureInfo.InvariantCulture, $"definitely under min_days={minimum}");
                }

                if (problem is null)
                {
                    continue;
                }

                violations.Add(new ChronoViolation
                {
                    Kind = ChronoRuleKind.Duration,
                    Severity = rule.Severity,
                    Message = string.Create(
                        CultureInfo.InvariantCulture,
                        $"chronology: elapsed days from '{from.ClaimKey}={from.Value}' to '{to.ClaimKey}={to.Value}' "
                            + $"(between {gapMinimum} and {gapMaximum}) {problem}"),
                    Rule = ruleDetail,
                    Chain = [from.ToDetail("from"), to.ToDetail("to")],
                    Extras = new JsonObject
                    {
                        ["gap_days_min"] = gapMinimum,
                        ["gap_days_max"] = gapMaximum,
                    },
                    CandidateClaims = CandidateClaims(from, to),
                });
            }
        }
    }
}
