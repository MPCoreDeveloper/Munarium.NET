namespace Munarium.Commands;

using SharpDispatch;

/// <summary>
/// A command to record a claim in the ledger.
/// </summary>
/// <remarks>
/// Governance runs on this path: the claim is either asserted or recorded as disputed. A blocked
/// claim is never silently dropped, which is why the command carries the claim itself rather than
/// a pre-judged instruction.
/// </remarks>
public sealed record RecordClaimCommand : ICommand
{
    /// <summary>Gets the stream the claim belongs to.</summary>
    public required string Stream { get; init; }

    /// <summary>Gets the claim identifier, unique within the stream.</summary>
    public required string ClaimId { get; init; }

    /// <summary>Gets the claim as stated.</summary>
    public required string Statement { get; init; }

    /// <summary>Gets who is asserting the claim.</summary>
    public required string Actor { get; init; }
}
