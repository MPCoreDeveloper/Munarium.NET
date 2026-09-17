namespace Munarium.Chronology;

using System.Text.Json.Nodes;

/// <summary>
/// Evaluates the declared chronology rules over a timeline of parsed assertions.
/// </summary>
/// <remarks>
/// Pure and deterministic: the only outside input is <c>now</c>, and passing
/// <see langword="null"/> for it disables the deadline-absence check entirely. Rules that compare two
/// assertions fire only when at least one of the two came from the candidate under review - a
/// contradiction between two facts of an existing corpus is history, and re-filing it on every write
/// would make the gate unusable. The absence check is the exception: it is clock-driven and re-files
/// while the deadline stays missed, because there is no write to attribute it to.
/// </remarks>
public static partial class ChronologyEvaluator
{
    /// <summary>
    /// Evaluates every declared rule.
    /// </summary>
    /// <param name="events">The timeline: the snapshot's facts overlaid with the candidate's claims.</param>
    /// <param name="rules">The declared rules.</param>
    /// <param name="now">The clock the absence check reads, or <see langword="null"/> to disable it.</param>
    /// <returns>The violations, in rule-family order.</returns>
    public static IReadOnlyList<ChronoViolation> Evaluate(
        IReadOnlyList<ChronoEvent> events,
        ChronologyRules rules,
        DateOnly? now)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(rules);

        // One canonical order for every rule, so the same timeline produces the same violations in the
        // same order whatever order the events were collected in.
        var timeline = events
            .OrderBy(item => item.Subject, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();

        var violations = new List<ChronoViolation>();

        EvaluateOrder(timeline, rules, violations);
        EvaluateContains(timeline, rules, violations);
        EvaluateOverlap(timeline, rules, violations);
        EvaluateDeadlines(timeline, rules, now, violations);
        EvaluateDurations(timeline, rules, violations);

        return violations;
    }

    private static void EvaluateOrder(
        List<ChronoEvent> timeline,
        ChronologyRules rules,
        List<ChronoViolation> violations)
    {
        foreach (var rule in rules.Order)
        {
            foreach (var (before, after) in Pairs(timeline, rule.Before, rule.After))
            {
                if (!InvolvesCandidate(before, after) || !after.Interval.DefinitelyBefore(before.Interval))
                {
                    continue;
                }

                violations.Add(new ChronoViolation
                {
                    Kind = ChronoRuleKind.Order,
                    Severity = rule.Severity,
                    Message = $"chronology: '{after.ClaimKey}={after.Value}' must come after "
                        + $"'{before.ClaimKey}={before.Value}' but is definitely before it",
                    Rule = new JsonObject
                    {
                        ["order"] = new JsonObject { ["before"] = rule.Before, ["after"] = rule.After },
                    },
                    Chain = [before.ToDetail("before"), after.ToDetail("after")],
                    Extras = [],
                    CandidateClaims = CandidateClaims(before, after),
                });
            }
        }
    }

    private static void EvaluateContains(
        List<ChronoEvent> timeline,
        ChronologyRules rules,
        List<ChronoViolation> violations)
    {
        foreach (var rule in rules.Contains)
        {
            foreach (var (outer, inner) in Pairs(timeline, rule.Outer, rule.Inner))
            {
                if (!InvolvesCandidate(outer, inner))
                {
                    continue;
                }

                // Certain only when neither side is hedged: "circa 1943" outside a stated period is not
                // a containment failure, it is an answer the dates cannot give.
                var certain = !outer.Interval.Uncertain && !inner.Interval.Uncertain;

                if (!certain || inner.Interval.Overlaps(outer.Interval))
                {
                    continue;
                }

                violations.Add(new ChronoViolation
                {
                    Kind = ChronoRuleKind.Contains,
                    Severity = rule.Severity,
                    Message = $"chronology: '{inner.ClaimKey}={inner.Value}' must fall within "
                        + $"'{outer.ClaimKey}={outer.Value}' but is definitely outside it",
                    Rule = new JsonObject
                    {
                        ["contains"] = new JsonObject { ["outer"] = rule.Outer, ["inner"] = rule.Inner },
                    },
                    Chain = [outer.ToDetail("outer"), inner.ToDetail("inner")],
                    Extras = [],
                    CandidateClaims = CandidateClaims(outer, inner),
                });
            }
        }
    }

    private static void EvaluateOverlap(
        List<ChronoEvent> timeline,
        ChronologyRules rules,
        List<ChronoViolation> violations)
    {
        foreach (var rule in rules.ForbidOverlap)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (first, second) in Pairs(timeline, rule.A, rule.B))
            {
                // When both patterns match both events the same pair arrives twice, and a symmetric rule
                // must not report one overlap as two findings.
                var pairId = string.CompareOrdinal(first.ClaimKey, second.ClaimKey) <= 0
                    ? string.Concat(first.ClaimKey, "\u0000", second.ClaimKey)
                    : string.Concat(second.ClaimKey, "\u0000", first.ClaimKey);

                if (!seen.Add(pairId) || !InvolvesCandidate(first, second))
                {
                    continue;
                }

                // Day precision on both sides: two month-long assertions overlapping is normal, two
                // dated events occupying the same day is the thing the rule is about.
                var definite = !first.Interval.Uncertain
                    && !second.Interval.Uncertain
                    && first.Interval.Precision is TemporalPrecision.Day
                    && second.Interval.Precision is TemporalPrecision.Day;

                if (!definite || !first.Interval.Overlaps(second.Interval))
                {
                    continue;
                }

                violations.Add(new ChronoViolation
                {
                    Kind = ChronoRuleKind.Overlap,
                    Severity = rule.Severity,
                    Message = $"chronology: '{first.ClaimKey}={first.Value}' and '{second.ClaimKey}={second.Value}' "
                        + "must not overlap but definitely do",
                    Rule = new JsonObject
                    {
                        ["forbid_overlap"] = new JsonObject { ["a"] = rule.A, ["b"] = rule.B },
                    },
                    Chain = [first.ToDetail("a"), second.ToDetail("b")],
                    Extras = [],
                    CandidateClaims = CandidateClaims(first, second),
                });
            }
        }
    }

    /// <summary>
    /// Pairs every event a target selects with every event the other target selects.
    /// </summary>
    /// <remarks>
    /// When neither target is absolute the pairing stays within one subject, which is what keeps a
    /// wildcard rule about a subject's own dates from comparing it with its neighbour's. An event never
    /// pairs with itself: a rule comparing a key with itself would otherwise fire on every claim.
    /// </remarks>
    private static IEnumerable<(ChronoEvent First, ChronoEvent Second)> Pairs(
        List<ChronoEvent> timeline,
        string targetA,
        string targetB)
    {
        var globalPairing = ChronologyPattern.IsAbsoluteTarget(targetA)
            || ChronologyPattern.IsAbsoluteTarget(targetB);

        foreach (var first in timeline)
        {
            if (!Matches(targetA, first))
            {
                continue;
            }

            foreach (var second in timeline)
            {
                if (string.Equals(second.ClaimKey, first.ClaimKey, StringComparison.Ordinal)
                    || !Matches(targetB, second)
                    || (!globalPairing && !string.Equals(first.Subject, second.Subject, StringComparison.Ordinal)))
                {
                    continue;
                }

                yield return (first, second);
            }
        }
    }

    private static bool Matches(string target, ChronoEvent candidate) =>
        ChronologyPattern.IsAbsoluteTarget(target)
            ? string.Equals(
                candidate.ClaimKey.ToLowerInvariant(),
                target.ToLowerInvariant(),
                StringComparison.Ordinal)
            : ChronologyPattern.KeyPatternMatches(target, candidate.Key);

    private static bool InvolvesCandidate(ChronoEvent first, ChronoEvent second) =>
        first.Origin is ChronoOrigin.Candidate || second.Origin is ChronoOrigin.Candidate;

    private static IReadOnlyList<string> CandidateClaims(params ChronoEvent[] events) =>
    [
        .. events
            .Where(item => item.Origin is ChronoOrigin.Candidate)
            .Select(item => item.ClaimKey),
    ];
}
