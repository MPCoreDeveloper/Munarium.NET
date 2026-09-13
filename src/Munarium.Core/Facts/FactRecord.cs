namespace Munarium.Facts;

using Munarium.Governance;

/// <summary>
/// A fact as the ledger records it: the claim, the lineage that decides which facts supersede each
/// other, and the governance outcome it was recorded under.
/// </summary>
/// <remarks>
/// A correction is a new fact on the same <see cref="Lineage"/> - never an update. The lineage is
/// the shape-declared identity (in the original design,
/// <c>spec.fact.supersession.identity: [contract_id, clause_type]</c>), so resolution needs no
/// mutable state: the highest global position on a lineage, at or below the pin, wins.
/// </remarks>
public sealed record FactRecord
{
    /// <summary>Gets the claim identifier, unique within its stream.</summary>
    public required string ClaimId { get; init; }

    /// <summary>Gets the lineage key: facts sharing it supersede one another.</summary>
    public required string Lineage { get; init; }

    /// <summary>Gets the fact as stated.</summary>
    public required string Statement { get; init; }

    /// <summary>Gets who asserted the fact.</summary>
    public required string Actor { get; init; }

    /// <summary>Gets the gate that blocked the fact, or an empty string when it was permitted.</summary>
    public required string Gate { get; init; }

    /// <summary>Gets why the gate blocked the fact, or an empty string when it was permitted.</summary>
    public required string Reason { get; init; }

    /// <summary>Gets a value indicating whether governance blocked this fact.</summary>
    public bool IsDisputed => !string.IsNullOrEmpty(Gate);

    /// <summary>
    /// Returns the governance verdict this fact was recorded under.
    /// </summary>
    /// <returns><see cref="Permitted"/> or <see cref="Blocked"/>.</returns>
    public ClaimVerdict Verdict() => IsDisputed ? new Blocked(Gate, Reason) : Permitted.Instance;
}
