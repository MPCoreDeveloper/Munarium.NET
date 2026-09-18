namespace Munarium.Sessions;

/// <summary>
/// One turn of a conversation, as the deployment records it.
/// </summary>
/// <remarks>
/// The payloads are JSON the store does not interpret: hits, the per-collection provenance envelopes, the completion
/// audit and the hierarchy decision belong to the planes that produced them, and a store that parsed them would be a
/// second place they are defined. What the store owns is the ordinal and the row.
/// <para>
/// The hierarchy decision is on the turn rather than inside the completion audit for a reason the original spells out:
/// the audit capture is capped, so the large, layered turns - the ones whose bodies get summarized away - are exactly
/// the ones whose decision has to survive somewhere else. A turn that ran no research profile has none, and absence is
/// recorded as absence rather than as an empty decision, because an empty decision would claim a hierarchy ran and
/// decided nothing.
/// </para>
/// </remarks>
public sealed record TurnRecord
{
    /// <summary>Gets the tenant, which is half of the turn's key.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the session the turn belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the turn's position in the conversation, one-based.
    /// </summary>
    /// <remarks>
    /// Allocated by the store and not by the caller - that is the whole reason the insert computes it - so a value
    /// arriving here is the store's answer and not a request.
    /// </remarks>
    public int Ordinal { get; init; }

    /// <summary>Gets the uid the turn is attributed to.</summary>
    public required string Uid { get; init; }

    /// <summary>Gets the question as asked.</summary>
    public required string Query { get; init; }

    /// <summary>Gets the collections actually searched, after access filtering.</summary>
    public required IReadOnlyList<string> CollectionsSearched { get; init; }

    /// <summary>Gets the merged hits, as JSON.</summary>
    public required string HitsJson { get; init; }

    /// <summary>Gets the per-collection provenance envelopes, as JSON.</summary>
    public required string EnvelopeJson { get; init; }

    /// <summary>Gets the completion audit and answer, as JSON, when a model answered.</summary>
    public string? CompletionJson { get; init; }

    /// <summary>Gets the evidence-hierarchy decision, as JSON, when a research profile ran.</summary>
    public string? HierarchyJson { get; init; }

    /// <summary>Gets when the turn was recorded, stamped by the store.</summary>
    public string? CreatedAt { get; init; }
}
