namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// Something the memory owes later: it was set up in one scope and has to be paid off by another.
/// </summary>
/// <remarks>
/// Promises are the ledger's memory of its own debts, which is what lets a later unit be judged on
/// whether it paid them. <see cref="FulfilledSequence"/> is kept separately from
/// <see cref="Status"/> for the reason every pin in this kernel exists: a promise fulfilled after the
/// pin has to read back OPEN (see <c>PromiseRegistry.StatusAt</c>), so the fulfilment is a position
/// and not only a flag.
/// </remarks>
public sealed record Promise
{
    /// <summary>Gets the promise's identity.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the version the promise belongs to.</summary>
    public required string VersionId { get; init; }

    /// <summary>Gets the stable coordination key - the same promise restated is the same key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the kind of promise, which is the workload's own vocabulary.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets what was promised, in the actor's words.</summary>
    public required string Description { get; init; }

    /// <summary>Gets the scope the promise was made in, when it was made in one.</summary>
    public string? OriginScope { get; init; }

    /// <summary>Gets the scope by which the promise is due, when a scope was named.</summary>
    public string? DueScope { get; init; }

    /// <summary>Gets where the promise stands.</summary>
    public PromiseStatus Status { get; init; } = PromiseStatus.Open;

    /// <summary>Gets the ledger position the promise was registered at.</summary>
    public required SequenceNumber Sequence { get; init; }

    /// <summary>Gets the position the promise was fulfilled at, or <see langword="null"/> while unpaid.</summary>
    public SequenceNumber? FulfilledSequence { get; init; }
}
