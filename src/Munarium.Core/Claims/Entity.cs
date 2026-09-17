namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// A thing the claims are about, resolved from the names the claims used.
/// </summary>
/// <remarks>
/// An entity is how the ledger answers "are these two claims about the same thing?" when the wording
/// differs - aliases, spelling, a rename. Merging is recorded as a pointer rather than a rewrite
/// (<see cref="MergedInto"/>), so a claim that named the absorbed entity still resolves and the
/// history of the merge is readable at a pin.
/// </remarks>
public sealed record Entity
{
    /// <summary>Gets the entity's identity.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the version the entity was resolved in.</summary>
    public required string VersionId { get; init; }

    /// <summary>Gets the name the entity is canonically known by.</summary>
    public required string CanonicalName { get; init; }

    /// <summary>Gets the kind of thing the entity is, when the workload names one.</summary>
    public string? EntityType { get; init; }

    /// <summary>Gets the other names the entity is known by.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Gets the ledger position the entity was resolved at.</summary>
    public required SequenceNumber Sequence { get; init; }

    /// <summary>Gets the entity this one was absorbed into, or <see langword="null"/>.</summary>
    public string? MergedInto { get; init; }
}
