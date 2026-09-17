namespace Munarium.Claims;

using Munarium.Facts;
using Munarium.Ledger;

/// <summary>
/// Projects a stored fact into the semantic claim the gates reason over.
/// </summary>
/// <remarks>
/// The ledger stores one entity and this is the second way of looking at it, which is why it is a
/// projection rather than a second model: the triple and the governance outcome are the same rows the
/// write path produced, not a copy that could drift from them.
/// <para>
/// The sequence a projected claim carries is the <em>global</em> position, because that is the axis the
/// pin is on and a snapshot's facts all have to share one: facts taken from different streams are only
/// comparable on the feed, not on their own stream positions.
/// </para>
/// </remarks>
public static class ClaimProjection
{
    /// <summary>
    /// Projects a fact.
    /// </summary>
    /// <param name="fact">The stored fact.</param>
    /// <param name="globalSequence">The fact's position in the ledger as a whole.</param>
    /// <returns>The claim.</returns>
    public static Claim Of(FactRecord fact, SequenceNumber globalSequence)
    {
        ArgumentNullException.ThrowIfNull(fact);

        return new Claim
        {
            Id = fact.ClaimId,
            VersionId = fact.VersionId,
            Sequence = globalSequence,
            ClaimType = fact.ClaimType,
            Subject = fact.Subject,
            Key = fact.Key,
            Value = fact.Value,
            ScopePath = fact.ScopePath,
            Status = fact.IsDisputed ? ClaimStatus.Disputed : ClaimStatus.Accepted,
            Provenance = fact.Provenance,
            SupersedesId = fact.SupersedesId,
            EntityId = fact.EntityId,
            EvidenceJson = fact.EvidenceJson,
            Confidence = fact.Confidence,
            ShapeRef = fact.ShapeRef,
            Origin = null,
        };
    }
}
