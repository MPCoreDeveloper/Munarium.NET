namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// The question a fact slice answers: which claims are current, as of when, where, and how many.
/// </summary>
/// <remarks>
/// The properties are the four things a reader can ask of the ledger, and they compose: a scope and a
/// pin together give "what did this chapter say at this point", which is the shape most callers want.
/// </remarks>
public sealed record ClaimQuery
{
    /// <summary>Gets the scope to read, matching the scope itself and everything under it.</summary>
    public string? ScopePrefix { get; init; }

    /// <summary>Gets the ledger position to read as of, or <see langword="null"/> for the head.</summary>
    public SequenceNumber? AsOfSequence { get; init; }

    /// <summary>
    /// Gets the statuses to read. Empty means accepted claims only.
    /// </summary>
    /// <remarks>
    /// The default is what makes a disputed claim invisible to a reader that did not ask for it: the
    /// refusal is in the ledger, and it stays out of the answer until someone asks about it by name.
    /// </remarks>
    public IReadOnlyList<ClaimStatus> Statuses { get; init; } = [];

    /// <summary>Gets how many claims to keep, counting from the newest, or <see langword="null"/> for all.</summary>
    public int? Limit { get; init; }
}
