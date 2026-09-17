namespace Munarium.Governance.Gates;

using System.Text.Json.Nodes;
using Munarium.Claims;

/// <summary>
/// Warns when produced text carries a marker of the machinery that produced it.
/// </summary>
/// <remarks>
/// An assistant that apologizes, refuses, or leaves a placeholder in its output has leaked the
/// conversation rather than done the work, and the text is then wrong in a way no later gate can
/// recover from - the value it asserts is about the assistant, not about the subject. The marker list
/// is deliberately short and literal: this is a warning a human reads, and a cleverer matcher would
/// mostly produce false positives on prose that legitimately discusses assistants.
/// <para>
/// The text is judged as a whole unit, which is why this rule cannot be expressed per claim.
/// </para>
/// </remarks>
public static class MetaLeakage
{
    /// <summary>The dotted rule identifier recorded with a finding from this gate.</summary>
    public const string RuleId = "gate.meta-leakage";

    private static readonly string[] Markers =
    [
        "as an ai",
        "as a language model",
        "i cannot assist",
        "i'm sorry, but",
        "[insert",
        "lorem ipsum",
    ];

    /// <summary>
    /// Judges a candidate's text.
    /// </summary>
    /// <param name="snapshot">The pinned view, which this gate does not read.</param>
    /// <param name="candidate">The candidate being judged.</param>
    /// <returns>One warning finding per marker present.</returns>
    public static IReadOnlyList<GateFinding> Evaluate(MeshSnapshot snapshot, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);

        var lowered = candidate.Text.ToLowerInvariant();
        var findings = new List<GateFinding>();

        foreach (var marker in Markers)
        {
            if (!lowered.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            findings.Add(new GateFinding
            {
                RuleId = RuleId,
                Severity = Severity.Warn,
                Message = $"output contains meta-leakage marker '{marker}'",
                ScopePath = candidate.ScopePath,
                Detail = new JsonObject { ["marker"] = marker },
            });
        }

        return findings;
    }
}
