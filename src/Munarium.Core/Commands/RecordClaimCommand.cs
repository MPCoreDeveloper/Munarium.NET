namespace Munarium.Commands;

using Munarium.Facts;
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
    /// <summary>Gets the version the claim belongs to.</summary>
    /// <remarks>
    /// A version is written to the stream of its own name, which is what makes a memory version and an
    /// append-only stream the same thing rather than two that have to be kept in step.
    /// </remarks>
    public required string VersionId { get; init; }

    /// <summary>Gets the claim identifier, unique within the version.</summary>
    public required string ClaimId { get; init; }

    /// <summary>
    /// Gets what the claim does to whatever the ledger already holds on its lineage.
    /// </summary>
    /// <remarks>
    /// A correction and a silent overwrite look identical to a ledger that is not told which one this is,
    /// which is why the type travels with the claim.
    /// </remarks>
    public ClaimType ClaimType { get; init; } = ClaimType.Unspecified;

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
