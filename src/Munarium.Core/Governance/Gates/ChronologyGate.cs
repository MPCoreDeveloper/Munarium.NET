namespace Munarium.Governance.Gates;

using System.Text.Json.Nodes;
using Munarium.Claims;
using Munarium.Chronology;

/// <summary>
/// The chronology family: calendar and ordering governance, armed by a declaration rather than always
/// on.
/// </summary>
/// <remarks>
/// Three properties separate this family from the other five. First, it runs only when a deployment has
/// declared rules, so a workload that has no chronology never pays for it and never sees a finding from
/// it. Second, it evaluates a timeline rather than a claim in isolation: the snapshot's accepted facts
/// are overlaid with the candidate's claims and corrections, so a correction that fixes a date clears
/// the violation in the same evaluation instead of requiring a second write. Third, only the newest
/// candidate-side contributor to a violation is named, because that is the claim the finding can
/// dispute - naming the whole chain would dispute facts of the ledger nobody is writing.
/// <para>
/// Findings carry the complete event chain, so the verdict can be checked rather than believed.
/// </para>
/// </remarks>
public static class ChronologyGate
{
    /// <summary>The rule identifier for ordering, containment and overlap violations.</summary>
    /// <remarks>
    /// The dotted vocabulary declares three identifiers for five rule kinds, so the three kinds that are
    /// about the shape of the timeline share one: a consumer that switches on the identifier still sees
    /// the kind in the finding's detail.
    /// </remarks>
    public const string OrderRuleId = "gate.chronology-order";

    /// <summary>The rule identifier for a missed or absent deadline.</summary>
    public const string DeadlineRuleId = "gate.chronology-deadline";

    /// <summary>The rule identifier for elapsed time outside its bounds.</summary>
    public const string DurationRuleId = "gate.chronology-duration";

    /// <summary>
    /// Evaluates the declared rules, with the snapshot's own instant as the clock.
    /// </summary>
    /// <remarks>
    /// This is the reproducible form: the clock the deadline-absence check reads is
    /// <see cref="MeshSnapshot.WrittenOn"/>, derived from the identities the snapshot carries, so the same
    /// snapshot produces the same findings for anyone who holds it - today, tomorrow, or in a review six
    /// months from now. When no identity in the snapshot is a ULID there is no such instant, and the
    /// absence check does not run; pass a date explicitly if one is known.
    /// </remarks>
    /// <param name="snapshot">The pinned view: its facts are the ledger side of the timeline.</param>
    /// <param name="candidate">The candidate under review.</param>
    /// <param name="rules">The declared rules.</param>
    /// <returns>One warning or block finding per violation.</returns>
    public static IReadOnlyList<GateFinding> Evaluate(
        MeshSnapshot snapshot,
        Candidate candidate,
        ChronologyRules rules)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Evaluate(snapshot, candidate, rules, snapshot.WrittenOn);
    }

    /// <summary>
    /// Evaluates the declared rules against a candidate and the snapshot it was written into.
    /// </summary>
    /// <remarks>
    /// <paramref name="now"/> is the clock the deadline-absence check reads, and there is deliberately no
    /// default for it: an absence check that quietly turns itself off when a caller forgets an argument is
    /// worse than one that either runs on the snapshot's own instant or is switched off on purpose. Pass
    /// <see langword="null"/> to say that explicitly.
    /// </remarks>
    /// <param name="snapshot">The pinned view: its facts are the ledger side of the timeline.</param>
    /// <param name="candidate">The candidate under review.</param>
    /// <param name="rules">The declared rules.</param>
    /// <param name="now">The clock the absence check reads, or <see langword="null"/> to disable it.</param>
    /// <returns>One warning or block finding per violation.</returns>
    public static IReadOnlyList<GateFinding> Evaluate(
        MeshSnapshot snapshot,
        Candidate candidate,
        ChronologyRules rules,
        DateOnly? now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rules);

        var timeline = Timeline(snapshot, candidate, rules);
        if (timeline.Count == 0)
        {
            return [];
        }

        var findings = new List<GateFinding>();

        foreach (var violation in ChronologyEvaluator.Evaluate(timeline, rules, now))
        {
            findings.Add(Finding(violation, candidate.ScopePath));
        }

        return findings;
    }

    private static GateFinding Finding(ChronoViolation violation, string? scopePath)
    {
        var detail = new JsonObject
        {
            ["kind"] = violation.Kind.ToWireName(),
            ["rule"] = violation.Rule.DeepClone(),
            ["chain"] = new JsonArray([.. violation.Chain.Select(link => link.DeepClone())]),
        };

        foreach (var (key, value) in violation.Extras)
        {
            detail[key] = value?.DeepClone();
        }

        if (violation.CandidateClaims.Count > 0)
        {
            // The newest candidate-side contributor is the claim under review; naming it is what lets a
            // block dispute the part of the chain that the write actually contains.
            detail["claim_key"] = violation.CandidateClaims[^1];
        }

        return new GateFinding
        {
            RuleId = RuleIdFor(violation.Kind),
            Severity = string.Equals(violation.Severity, ChronologySeverity.Block, StringComparison.OrdinalIgnoreCase)
                ? Severity.Block
                : Severity.Warn,
            Message = violation.Message,
            ScopePath = scopePath,
            Detail = detail,
        };
    }

    private static string RuleIdFor(ChronoRuleKind kind) => kind switch
    {
        ChronoRuleKind.Deadline => DeadlineRuleId,
        ChronoRuleKind.Duration => DurationRuleId,
        _ => OrderRuleId,
    };

    /// <summary>
    /// Builds the timeline: the snapshot's facts, overlaid with the candidate's claims and corrections.
    /// </summary>
    /// <remarks>
    /// A claim joins the timeline when its key looks temporal or when a rule names it exactly, and only
    /// when its value parses as a date. A value that does not parse removes the key rather than leaving
    /// the previous one in place: the latest assertion is what is being judged, and judging a value the
    /// ledger has already moved past would file a finding about a date nobody holds.
    /// </remarks>
    private static List<ChronoEvent> Timeline(MeshSnapshot snapshot, Candidate candidate, ChronologyRules rules)
    {
        var absoluteTargets = rules.AbsoluteTargets();
        var timeline = new SortedDictionary<string, ChronoEvent>(StringComparer.Ordinal);

        foreach (var fact in snapshot.Facts)
        {
            if (fact.Subject.Length > 0 || fact.Key.Length > 0)
            {
                Consider(timeline, absoluteTargets, rules, fact.Subject, fact.Key, fact.Value, fact.Id, ChronoOrigin.Ledger);
            }
        }

        foreach (var proposed in candidate.Claims.Concat(candidate.Corrections))
        {
            Consider(timeline, absoluteTargets, rules, proposed.Subject, proposed.Key, proposed.Value, null, ChronoOrigin.Candidate);
        }

        return [.. timeline.Values];
    }

    private static void Consider(
        SortedDictionary<string, ChronoEvent> timeline,
        IReadOnlyList<string> absoluteTargets,
        ChronologyRules rules,
        string subject,
        string key,
        string value,
        string? claimId,
        ChronoOrigin origin)
    {
        var claimKey = string.Concat(subject, ".", key);

        if (!rules.IsTemporalKey(key) && !absoluteTargets.Contains(claimKey.ToLowerInvariant()))
        {
            return;
        }

        if (ChronologyGrammar.Parse(value) is not { } when)
        {
            timeline.Remove(claimKey);
            return;
        }

        timeline[claimKey] = new ChronoEvent
        {
            Subject = subject,
            Key = key,
            Value = value,
            Interval = when,
            ClaimId = claimId,
            Origin = origin,
        };
    }
}
