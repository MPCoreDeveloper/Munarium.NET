namespace Munarium.Governance.Gates;

using System.Text.Json.Nodes;
using Munarium.Claims;

/// <summary>
/// Warns about a correction that targets a subject or detail the ledger never established.
/// </summary>
/// <remarks>
/// A correction claims that an earlier value was wrong, so it is only meaningful against a value that
/// exists. An orphaned one is not refused - it may be the first thing the ledger is told about a
/// subject, and refusing it would make a repair unrecordable - but it is surfaced, because a
/// correction with nothing to correct is usually a mistake about which subject the writer meant.
/// <para>
/// A known subject with an unknown detail is orphaned too: the correction names a property that
/// subject has never had, which is the same mistake one level down.
/// </para>
/// </remarks>
public static class OrphanedReference
{
    /// <summary>The dotted rule identifier recorded with a finding from this gate.</summary>
    public const string RuleId = "gate.orphaned-reference";

    /// <summary>
    /// Judges a candidate's corrections against the snapshot's facts.
    /// </summary>
    /// <param name="snapshot">The pinned view; its facts are the established canon.</param>
    /// <param name="candidate">The candidate being judged.</param>
    /// <returns>One warning finding per orphaned correction.</returns>
    public static IReadOnlyList<GateFinding> Evaluate(MeshSnapshot snapshot, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);

        var knownKeys = new HashSet<string>(StringComparer.Ordinal);
        var knownSubjects = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fact in snapshot.Facts)
        {
            knownKeys.Add(fact.ClaimKey);
            knownSubjects.Add(fact.Subject);
        }

        var findings = new List<GateFinding>();

        foreach (var correction in candidate.Corrections)
        {
            var key = correction.ClaimKey;

            // Either the subject was never established, or this detail of it was not.
            if (knownSubjects.Contains(correction.Subject) && knownKeys.Contains(key))
            {
                continue;
            }

            findings.Add(new GateFinding
            {
                RuleId = RuleId,
                Severity = Severity.Warn,
                Message = $"correction targets '{key}' but no such canon was established",
                ScopePath = candidate.ScopePath,
                Detail = new JsonObject { ["claim_key"] = key },
            });
        }

        return findings;
    }
}
