namespace Munarium.Evidence;

/// <summary>
/// A source of evidence for one layer.
/// </summary>
/// <remarks>
/// The seam. Documents, governed tables, counts and ledger facts all arrive through this one shape, which is what
/// lets a research profile order them without the pipeline knowing what any of them are.
/// <para>
/// <see cref="FetchAsync"/> returns a <em>refusal block</em>, not an error, for anything the caller should know
/// about. A provider that cannot answer - denied, stale, unreachable, out of time - has still told the turn
/// something, and often something the answer has to disclose. An exception is reserved for a bug: a malformed
/// plan, a poisoned lock, an invariant broken inside the provider itself.
/// </para>
/// </remarks>
public interface IEvidenceProvider
{
    /// <summary>Gets the provider's stable identity, used in the hierarchy decision and in progress reporting.</summary>
    string Id { get; }

    /// <summary>
    /// Reports whether this provider can serve a pinned source.
    /// </summary>
    /// <remarks>
    /// Checked when a profile is applied, so a broken binding fails then rather than in the middle of a turn.
    /// </remarks>
    /// <param name="source">The pinned source.</param>
    /// <returns><see langword="true"/> when the provider can serve it.</returns>
    bool CanServe(string source);

    /// <summary>
    /// Fetches a layer's evidence.
    /// </summary>
    /// <param name="layer">The layer to serve.</param>
    /// <param name="intent">What the turn intends to find out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The block, which is a refusal when the provider could not answer.</returns>
    ValueTask<EvidenceBlock> FetchAsync(
        EvidenceLayer layer,
        QueryIntent intent,
        CancellationToken cancellationToken = default);
}
