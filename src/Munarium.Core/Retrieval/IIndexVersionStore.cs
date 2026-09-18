namespace Munarium.Retrieval;

/// <summary>
/// Persistence for index versions.
/// </summary>
/// <remarks>
/// Separate from the ledger's storage seam and from the evidence plane's: an index version is a retrieval fact,
/// and the ledger neither knows nor needs to know which index answered a question.
/// <para>
/// The implementations are the retrieval slice's; the kernel owns the seam, the identity and the cutover rules.
/// </para>
/// </remarks>
public interface IIndexVersionStore
{
    /// <summary>
    /// Records a version, or returns the version already recorded under its identity.
    /// </summary>
    /// <remarks>
    /// A rebuild of the same identity is the same version, so this is idempotent - which is what lets a caller
    /// build without first asking whether it already did. Two things may still move on an existing version and
    /// nothing else: its watermark advances to a newer build's position and never backwards, because a rebuild
    /// reads a later ledger state and can never un-read it. Content, manifest and identity do not move, or an
    /// answer's provenance would stop meaning anything.
    /// </remarks>
    /// <param name="version">The version to record, as the catalogue minted it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recorded version, which carries the identity the store holds.</returns>
    ValueTask<IndexVersion> RegisterAsync(IndexVersion version, CancellationToken cancellationToken = default);

    /// <summary>Reads a version by its identity.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="indexVersionId">The version's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or <see langword="null"/> when there is none.</returns>
    ValueTask<IndexVersion?> GetAsync(
        string tenant,
        string indexVersionId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a collection's live version.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="collectionId">The collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active version, or <see langword="null"/> when the collection has none.</returns>
    ValueTask<IndexVersion?> ActiveAsync(
        string tenant,
        string collectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cuts a collection over to a version: exactly one version is active per collection.
    /// </summary>
    /// <remarks>
    /// Atomic and reversible, because a cutover is a serving decision rather than a deletion. The superseded
    /// version keeps the instant it stopped being live, so how long it was serving stays answerable, and it stays
    /// readable - an envelope issued while it was live still resolves.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="collectionId">The collection.</param>
    /// <param name="indexVersionId">The version to make live.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activated version, or <see langword="null"/> when no such version belongs to the collection.</returns>
    ValueTask<IndexVersion?> ActivateAsync(
        string tenant,
        string collectionId,
        string indexVersionId,
        CancellationToken cancellationToken = default);
}
