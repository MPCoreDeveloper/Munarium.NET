namespace Munarium.Governance.Gates;

using System.Text.Json.Nodes;
using Munarium.Claims;

/// <summary>
/// Refuses a claim or correction whose value contradicts a locked anchor for the same detail.
/// </summary>
/// <remarks>
/// This gate runs first, and the runner drops a ledger-conflict finding for a detail it has already
/// reported: a claim that contradicts a lock is reported as an anchor finding, because telling the
/// operator to supersede a value that is locked is advice that cannot be followed.
/// <para>
/// A correction is judged here as well. Declaring that a value is being corrected does not unlock it:
/// the anchor is the memory of a decision that the detail does not change, so the only ways past it
/// are a released lock or a value that agrees.
/// </para>
/// </remarks>
public static class AnchorConsistency
{
    /// <summary>The dotted rule identifier recorded with a finding from this gate.</summary>
    public const string RuleId = "gate.anchor-consistency";

    /// <summary>
    /// Judges a candidate against the snapshot's locked anchors.
    /// </summary>
    /// <param name="snapshot">The pinned view; its anchors are the locked ones.</param>
    /// <param name="candidate">The candidate being judged.</param>
    /// <returns>One block finding per contradicting proposal.</returns>
    public static IReadOnlyList<GateFinding> Evaluate(MeshSnapshot snapshot, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);

        var findings = new List<GateFinding>();

        foreach (var proposed in candidate.Claims.Concat(candidate.Corrections))
        {
            var key = proposed.ClaimKey;

            if (!snapshot.Anchors.TryGetValue(key, out var anchor) ||
                ClaimText.ValuesEquivalent(proposed.Value, anchor.LockedValue))
            {
                continue;
            }

            findings.Add(new GateFinding
            {
                RuleId = RuleId,
                Severity = Severity.Block,
                Message = $"claim '{key}={proposed.Value}' contradicts locked anchor value '{anchor.LockedValue}'",
                ScopePath = candidate.ScopePath,
                Detail = new JsonObject
                {
                    ["claim_key"] = key,
                    ["proposed_value"] = proposed.Value,
                    ["locked_value"] = anchor.LockedValue,
                    ["anchor_id"] = anchor.Id,
                },
            });
        }

        return findings;
    }
}
