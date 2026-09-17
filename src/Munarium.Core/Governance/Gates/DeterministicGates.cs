namespace Munarium.Governance.Gates;

using Munarium.Claims;

/// <summary>
/// The five always-on gates, run together in the order that makes their findings read correctly.
/// </summary>
/// <remarks>
/// The order is part of the answer, not an implementation detail. Anchor consistency runs first
/// because a locked value is not superseded by anything, so a ledger-conflict finding for the same
/// detail would recommend a supersession that cannot happen; the runner therefore drops it rather than
/// reporting two rules for one disagreement.
/// <para>
/// The sixth family - chronology - is deliberately not here. It is declaratively armed, so a
/// deployment that has declared no chronology rules runs these five and nothing else; the composition
/// that has rules calls it right after this.
/// </para>
/// </remarks>
public static class DeterministicGates
{
    /// <summary>
    /// Runs gates 1-5 over a candidate and dedups their findings.
    /// </summary>
    /// <param name="snapshot">The pinned view the gates read.</param>
    /// <param name="candidate">The candidate being judged.</param>
    /// <returns>The findings, in rule order.</returns>
    public static IReadOnlyList<GateFinding> Run(MeshSnapshot snapshot, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);

        var findings = new List<GateFinding>(AnchorConsistency.Evaluate(snapshot, candidate));
        var anchored = ClaimKeys(findings);

        foreach (var finding in LedgerConflict.Evaluate(snapshot, candidate))
        {
            // The anchor finding subsumes it: same detail, and a reason that can be acted on.
            if (finding.ClaimKey is { } key && anchored.Contains(key))
            {
                continue;
            }

            findings.Add(finding);
        }

        findings.AddRange(OrphanedReference.Evaluate(snapshot, candidate));
        findings.AddRange(MetaLeakage.Evaluate(snapshot, candidate));
        findings.AddRange(LexicalSimilarity.Evaluate(snapshot, candidate));

        return findings;
    }

    /// <summary>
    /// Downgrades every block to a warning, for a corpus absorbed after the fact.
    /// </summary>
    /// <remarks>
    /// A backfill meets contradictions that already exist in the corpus it is importing, and refusing
    /// to import them would mean refusing the import. Downgrading rather than skipping is what keeps
    /// the conflict visible in the ledger: it is surfaced, never blocking. Because a finding is
    /// immutable, this returns a new set; the caller assigns it.
    /// </remarks>
    /// <param name="findings">The findings to downgrade.</param>
    /// <returns>The findings, with no block among them.</returns>
    public static IReadOnlyList<GateFinding> DowngradeBlocks(IReadOnlyList<GateFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return
        [
            .. findings.Select(finding => finding.Severity is Severity.Block
                ? finding with { Severity = Severity.Warn }
                : finding),
        ];
    }

    /// <summary>
    /// Collects the claim keys that carry a block - the claims the accept path records as disputed.
    /// </summary>
    /// <remarks>
    /// Only a finding that names a claim can dispute one, which is why a finding without a claim key
    /// - a meta-leakage warning about a unit of text - is skipped instead of being attributed to
    /// every claim in the unit.
    /// </remarks>
    /// <param name="findings">The findings to read.</param>
    /// <returns>The blocked claim keys.</returns>
    public static IReadOnlySet<string> BlockedClaimKeys(IReadOnlyList<GateFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return ClaimKeys(findings.Where(finding => finding.Severity is Severity.Block));
    }

    private static HashSet<string> ClaimKeys(IEnumerable<GateFinding> findings)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            if (finding.ClaimKey is { } key)
            {
                keys.Add(key);
            }
        }

        return keys;
    }
}
