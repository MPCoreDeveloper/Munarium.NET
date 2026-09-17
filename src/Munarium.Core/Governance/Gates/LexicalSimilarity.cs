namespace Munarium.Governance.Gates;

using System.Globalization;
using System.Text.Json.Nodes;
using Munarium.Claims;
using Munarium.Text;

/// <summary>
/// Warns when a unit's text is a near-duplicate of an earlier unit's.
/// </summary>
/// <remarks>
/// Repetition is the failure mode a long generation falls into: the same paragraph, or the same
/// paragraph with one word changed, arriving as if it were new. It is a warning rather than a block
/// because a workload may legitimately restate something - a summary quoting the clause it summarizes
/// - and the operator, not the kernel, knows which one this is.
/// <para>
/// The threshold is deliberately high (0.9). The ratio is over characters rather than tokens, so a
/// lower bar would fire on any two units that share a vocabulary, while at 0.9 it fires on text that
/// is recognizably the same text.
/// </para>
/// </remarks>
public static class LexicalSimilarity
{
    /// <summary>The dotted rule identifier recorded with a finding from this gate.</summary>
    public const string RuleId = "gate.lexical-similarity";

    /// <summary>The ratio at or above which two units count as the same text.</summary>
    public const double Threshold = 0.9;

    /// <summary>
    /// Judges a candidate's text against the units already published.
    /// </summary>
    /// <param name="snapshot">The pinned view, which this gate does not read.</param>
    /// <param name="candidate">The candidate being judged, carrying the previous units' text.</param>
    /// <returns>One warning finding per previous unit the text repeats.</returns>
    public static IReadOnlyList<GateFinding> Evaluate(MeshSnapshot snapshot, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);

        if (string.IsNullOrWhiteSpace(candidate.Text))
        {
            return [];
        }

        var findings = new List<GateFinding>();

        for (var index = 0; index < candidate.PreviousTexts.Count; index++)
        {
            var ratio = SimilarityRatio.Compare(candidate.Text, candidate.PreviousTexts[index]);
            if (ratio < Threshold)
            {
                continue;
            }

            findings.Add(new GateFinding
            {
                RuleId = RuleId,
                Severity = Severity.Warn,
                Message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"output is {ratio * 100.0:F0}% similar to previous unit {index} (threshold {Threshold * 100.0:F0}%)"),
                ScopePath = candidate.ScopePath,
                Detail = new JsonObject
                {
                    ["previous_index"] = index,
                    ["ratio"] = ratio,
                },
            });
        }

        return findings;
    }
}
