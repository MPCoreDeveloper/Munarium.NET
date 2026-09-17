namespace Munarium.Claims;

/// <summary>
/// How often a pattern has been used across a whole document, and the ceiling it is held to.
/// </summary>
/// <remarks>
/// A counter is absolute rather than per-scope: the budget is a property of the document, so a scope
/// cannot stay inside it by moving a repetition elsewhere. <see cref="Budget"/> is optional, and an
/// unbudgeted counter is still counted - that is what makes the total usable when a budget is added
/// later, without having re-read the document.
/// </remarks>
public sealed record CounterTotal
{
    /// <summary>Gets the counter's key, which is the pattern being counted.</summary>
    public required string Key { get; init; }

    /// <summary>Gets how many times the pattern occurs across the document.</summary>
    public required ulong Total { get; init; }

    /// <summary>Gets the ceiling the total is held to, or <see langword="null"/> when unbudgeted.</summary>
    public ulong? Budget { get; init; }

    /// <summary>Gets a value indicating whether the total is over its budget.</summary>
    public bool IsOverBudget => Budget is { } budget && Total > budget;
}
