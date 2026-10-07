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
                EvaluateDeadline(timeline, rule, ruleDetail, globalPairing, from, now, violations);
            }
        }
    }

    /// <summary>Judges one rule's due-from event against every arrival the timeline holds.</summary>
    /// <param name="timeline">The timeline.</param>
    /// <param name="rule">The deadline rule.</param>
    /// <param name="ruleDetail">The rule as the violation records it.</param>
    /// <param name="globalPairing">Whether the target pairs across subjects rather than within one.</param>
    /// <param name="from">The due-from event, when it is one and the rule's target, and is certain.</param>
    /// <param name="now">The clock, or <see langword="null"/> to check absences only against what is known.</param>
    /// <param name="violations">The violations so far.</param>
    private static void EvaluateDeadline(
        List<ChronoEvent> timeline,
        DeadlineRule rule,
        JsonObject ruleDetail,
        bool globalPairing,
        ChronoEvent from,
        DateOnly? now,
        List<ChronoViolation> violations)
    {
        if (!Matches(rule.DueFrom, from) || from.Interval.Uncertain)
        {
            return;
        }

        if (AddDaysChecked(from.Interval.End, rule.WithinDays) is not { } deadline)
        {
            return;
        }

        var arrivals = ArrivalsOf(timeline, rule, from, globalPairing);

        if (arrivals.Count == 0)
        {
            ReportAbsence(from, rule, deadline, now, ruleDetail, violations);
            return;
        }

        foreach (var arrival in arrivals.Where(item => InvolvesCandidate(from, item)))
        {
            ReportLateArrival(rule, ruleDetail, from, arrival, deadline, violations);
        }
    }

    /// <summary>The timeline's arrivals for a rule: other claims that match it, paired by subject unless the target is absolute.</summary>
    /// <param name="timeline">The timeline.</param>
    /// <param name="rule">The rule.</param>
    /// <param name="from">The due-from event whose arrival is being looked for.</param>
    /// <param name="globalPairing">Whether the pair is matched by key alone.</param>
    /// <returns>The arrivals, in the order the timeline holds them.</returns>
    private static List<ChronoEvent> ArrivalsOf(
        List<ChronoEvent> timeline,
        DeadlineRule rule,
        ChronoEvent from,
        bool globalPairing) =>
        [
            .. timeline.Where(item =>
                !string.Equals(item.ClaimKey, from.ClaimKey, StringComparison.Ordinal)
                && Matches(rule.Key, item)
                && (globalPairing || string.Equals(item.Subject, from.Subject, StringComparison.Ordinal))),
        ];

    /// <summary>Records an arrival that is definitely later than the deadline.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="ruleDetail">The rule as the violation records it.</param>
    /// <param name="from">The due-from event.</param>
    /// <param name="arrival">The arrival.</param>
    /// <param name="deadline">The deadline the arrival is measured against.</param>
    /// <param name="violations">The violations so far.</param>
    /// <remarks>
    /// An arrival whose interval is uncertain is undecided rather than late, and one whose interval starts on the
    /// deadline is on time: the deadline is the last day that meets it.
    /// </remarks>
    private static void ReportLateArrival(
        DeadlineRule rule,
        JsonObject ruleDetail,
        ChronoEvent from,
        ChronoEvent arrival,
        DateOnly deadline,
        List<ChronoViolation> violations)
    {
        if (arrival.Interval.Uncertain || arrival.Interval.Start <= deadline)
        {
            return;
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
