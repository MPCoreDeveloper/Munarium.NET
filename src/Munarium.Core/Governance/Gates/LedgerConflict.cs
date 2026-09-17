namespace Munarium.Governance.Gates;

using System.Text.Json.Nodes;
using Munarium.Claims;

/// <summary>
/// Refuses a plain claim that contradicts the accepted value the ledger already holds for its claim
/// key.
/// </summary>
/// <remarks>
/// A claim that names what it supersedes is exempt: that is the difference between a value changing on
/// purpose - a status transition, a correction - and a value being overwritten without anyone saying
/// so. The gate therefore reads the declaration, not the intent behind the wording.
/// <para>
/// This is the candidate-plane counterpart of the command path's ledger-conflict gate: the same rule,
/// judged over a whole unit of proposals before anything is written, and reported as a finding rather
/// than as a verdict.
/// </para>
/// </remarks>
public static class LedgerConflict
{
    /// <summary>The dotted rule identifier recorded with a finding from this gate.</summary>
    public const string RuleId = "gate.ledger-conflict";

    /// <summary>
    /// Judges a candidate's plain claims against the snapshot's accepted facts.
    /// </summary>
    /// <param name="snapshot">The pinned view; its facts are seq-ascending and already resolved.</param>
    /// <param name="candidate">The candidate being judged.</param>
    /// <returns>One block finding per contradicting proposal.</returns>
    public static IReadOnlyList<GateFinding> Evaluate(MeshSnapshot snapshot, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);

        var findings = new List<GateFinding>();

        foreach (var proposed in candidate.Claims)
        {
            if (proposed.SupersedesId is not null)
            {
                continue;
            }

            var key = proposed.ClaimKey;

            // Facts are seq-ascending and resolved-current, so the last one for a key is the canon.
            var canon = snapshot.Facts.LastOrDefault(fact => string.Equals(fact.ClaimKey, key, StringComparison.Ordinal));
            if (canon is null || ClaimText.ValuesEquivalent(proposed.Value, canon.Value))
            {
                continue;
            }

            findings.Add(new GateFinding
            {
                RuleId = RuleId,
                Severity = Severity.Block,
                Message = $"claim '{key}={proposed.Value}' conflicts with accepted canon "
                    + $"'{key}={canon.Value}' (use a correction to supersede)",
                ScopePath = candidate.ScopePath,
                Detail = new JsonObject
                {
                    ["claim_key"] = key,
                    ["proposed_value"] = proposed.Value,
                    ["canon_value"] = canon.Value,
                    ["canon_claim_id"] = canon.Id,
                    ["canon_seq"] = canon.Sequence.Value,
                },
            });
        }

        return findings;
    }
}
