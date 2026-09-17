namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// A locked detail: while the anchor is locked, <see cref="DetailKey"/> may not drift from
/// <see cref="LockedValue"/>.
/// </summary>
/// <remarks>
/// An anchor is what makes a detail non-negotiable across later writing - a canonical founding date, a
/// contract's governing law - without freezing the whole subject. It is judged by the anchor gate
/// before any other gate sees the claim, so a contradiction against a lock is reported as an anchor
/// finding rather than a ledger conflict: the finding has to name the right reason.
/// </remarks>
public sealed record Anchor
{
    /// <summary>Gets the anchor's identity.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the version the anchor was locked in.</summary>
    public required string VersionId { get; init; }

    /// <summary>Gets the detail that is locked, as <c>subject.key</c>.</summary>
    public required string DetailKey { get; init; }

    /// <summary>Gets the value the detail is pinned to.</summary>
    public required string LockedValue { get; init; }

    /// <summary>Gets the scope the lock was taken at, when it was taken at one.</summary>
    public string? LockedAtScope { get; init; }

    /// <summary>Gets whether the lock still holds. Only a locked anchor is judged against.</summary>
    public AnchorStatus Status { get; init; } = AnchorStatus.Locked;

    /// <summary>Gets the ledger position the lock was taken at.</summary>
    public required SequenceNumber Sequence { get; init; }

    /// <summary>Gets the evidence the lock was taken on, as JSON text, or <see langword="null"/>.</summary>
    public string? EvidenceJson { get; init; }
}
