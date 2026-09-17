namespace Munarium.Chronology;

using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// The deadline half of the chronology evaluation, and the date arithmetic it needs.
/// </summary>
/// <remarks>
/// A deadline can be missed in two directions, and they are not the same finding. An arrival after the
/// deadline is a claim the candidate wrote, so the block can dispute it; an absent arrival is a state
/// of the world that no claim owns, so it warns and re-files while it stays true. Only the second
/// direction needs a clock, which is why passing <see langword="null"/> for it turns the absence check
/// off rather than turning the family off.
/// </remarks>
public static partial class ChronologyEvaluator
{
    private static void EvaluateDeadlines(
        List<ChronoEvent> timeline,
        ChronologyRules rules,
        DateOnly? now,
        List<ChronoViolation> violations)
    {
        foreach (var rule in rules.Deadlines)
        {
            var ruleDetail = new JsonObject
            {
                ["deadline"] = new JsonObject
                {
                    ["key"] = rule.Key,
                    ["due_from"] = rule.DueFrom,
                    ["within_days"] = rule.WithinDays,
                },
            };

            var globalPairing = ChronologyPattern.IsAbsoluteTarget(rule.Key)
                || ChronologyPattern.IsAbsoluteTarget(rule.DueFrom);

            foreach (var from in timeline)
            {
                if (!Matches(rule.DueFrom, from) || from.Interval.Uncertain)
                {
                    continue;
                }

                if (AddDaysChecked(from.Interval.End, rule.WithinDays) is not { } deadline)
                {
                    continue;
                }

                var arrivals = timeline
                    .Where(item =>
                        !string.Equals(item.ClaimKey, from.ClaimKey, StringComparison.Ordinal)
                        && Matches(rule.Key, item)
                        && (globalPairing || string.Equals(item.Subject, from.Subject, StringComparison.Ordinal)))
                    .ToList();

                if (arrivals.Count == 0)
                {
                    ReportAbsence(from, rule, deadline, now, ruleDetail, violations);
                    continue;
                }

                foreach (var arrival in arrivals.Where(item => InvolvesCandidate(from, item)))
                {
                    if (arrival.Interval.Uncertain || arrival.Interval.Start <= deadline)
                    {
                        continue;
                    }

                    violations.Add(new ChronoViolation
                    {
                        Kind = ChronoRuleKind.Deadline,
                        Severity = rule.Severity,
                        Message = $"chronology: '{arrival.ClaimKey}={arrival.Value}' is definitely later than the "
                            + $"deadline {Date(deadline)} ({rule.WithinDays} days after '{from.ClaimKey}={from.Value}')",
                        Rule = ruleDetail,
                        Chain = [from.ToDetail("due_from"), arrival.ToDetail("event")],
                        Extras = DeadlineExtras(deadline, clock: null),
                        CandidateClaims = CandidateClaims(from, arrival),
                    });
                }
            }
        }
    }

    private static void ReportAbsence(
        ChronoEvent from,
        DeadlineRule rule,
        DateOnly deadline,
        DateOnly? now,
        JsonObject ruleDetail,
        List<ChronoViolation> violations)
    {
        if (now is not { } clock || clock <= deadline)
        {
            return;
        }

        violations.Add(new ChronoViolation
        {
            Kind = ChronoRuleKind.Deadline,
            Severity = rule.Severity,
            Message = $"chronology: no '{rule.Key}' event within {rule.WithinDays} days of "
                + $"'{from.ClaimKey}={from.Value}' — deadline {Date(deadline)} passed as of {Date(clock)}",
            Rule = ruleDetail,
            Chain = [from.ToDetail("due_from")],
            Extras = DeadlineExtras(deadline, clock),
            CandidateClaims = CandidateClaims(from),
        });
    }

    private static JsonObject DeadlineExtras(DateOnly deadline, DateOnly? clock)
    {
        var extras = new JsonObject { ["deadline"] = Date(deadline) };

        if (clock is { } value)
        {
            extras["now"] = Date(value);
        }

        return extras;
    }

    private static string Date(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? AddDaysChecked(DateOnly date, long days)
    {
        if (days is < int.MinValue or > int.MaxValue)
        {
            return null;
        }

        try
        {
            return date.AddDays((int)days);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A deadline past the end of the calendar can be neither met nor missed, so it cannot
            // produce a certain violation either.
            return null;
        }
    }
}
