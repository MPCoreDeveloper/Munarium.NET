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

    /// <summary>Gets the name of the shape this claim is made under.</summary>
    /// <remarks>
    /// The shape supplies both halves of what the write path needs: the schema the body is validated
    /// against, and the identity fields that decide which facts supersede each other.
    /// </remarks>
    public required string Shape { get; init; }

    /// <summary>Gets the structured fact body, as JSON.</summary>
    /// <remarks>
    /// This is what the shape's schema validates, and what the shape's identity fields are read from
    /// to derive the lineage. The claim's human wording lives in <see cref="Statement"/>.
    /// </remarks>
    public required string Body { get; init; }

    /// <summary>Gets the claim as stated, in the actor's words.</summary>
    public required string Statement { get; init; }

    /// <summary>Gets who is asserting the claim.</summary>
    public required string Actor { get; init; }
}
