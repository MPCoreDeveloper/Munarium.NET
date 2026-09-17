namespace Munarium.Claims;

/// <summary>
/// The unit governance judges: everything a writer proposed for one scope, plus the raw text the
/// text-shaped rules read.
/// </summary>
/// <remarks>
/// A candidate is a unit of work rather than a claim, because two of the gates cannot see a claim at
/// all: meta-leakage and repetition are properties of the produced text, and judging them per claim
/// would let a unit pass by splitting itself. The proposals are split into plain claims and declared
/// corrections so each gate reads the plane it means to - a correction is judged for being orphaned,
/// a plain claim for conflicting.
/// <para>
/// <see cref="PreviousTexts"/> is what makes repetition judgeable: the rule is about this unit against
/// the ones already published, so the caller supplies them.
/// </para>
/// </remarks>
public sealed record Candidate
{
    /// <summary>Gets the scope the candidate was produced in, when it was produced in one.</summary>
    public string? ScopePath { get; init; }

    /// <summary>Gets the raw text the candidate produced.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the proposed claims, which are judged for conflicting.</summary>
    public IReadOnlyList<ProposedClaim> Claims { get; init; } = [];

    /// <summary>Gets the proposed corrections, which are judged for being orphaned.</summary>
    public IReadOnlyList<ProposedClaim> Corrections { get; init; } = [];

    /// <summary>Gets the text of the units already published, in order, for the repetition rule.</summary>
    public IReadOnlyList<string> PreviousTexts { get; init; } = [];
}
