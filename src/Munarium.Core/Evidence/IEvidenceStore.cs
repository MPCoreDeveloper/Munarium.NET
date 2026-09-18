namespace Munarium.Evidence;

/// <summary>
/// Persistence for the evidence plane.
/// </summary>
/// <remarks>
/// Separate from the ledger's storage seam on purpose: that one is the ledger, and evidence is not ledger data.
/// Merging them would put an artifact's retention clock in the same trait as appending a claim, and the two have
/// nothing to say to each other.
/// <para>
/// The implementations are the evidence slice's, in the same way the ledger's seam has one adapter per store;
/// the kernel owns the seam and the rules a manifest has to satisfy.
/// </para>
/// </remarks>
public interface IEvidenceStore
{
    /// <summary>
    /// Registers a manifest, or returns the existing artifact when the domain key already exists.
    /// </summary>
    /// <param name="artifact">The artifact to register, which is pending when a grant is wanted.</param>
    /// <param name="grant">The grant to issue, or <see langword="null"/> to commit immediately.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The seal outcome, which says whether this call created the artifact.</returns>
    ValueTask<SealOutcome> RegisterAsync(
        EvidenceArtifact artifact,
        EvidenceGrant? grant = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads an artifact.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="evidenceId">The artifact's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The artifact, or <see langword="null"/> when there is none.</returns>
    ValueTask<EvidenceArtifact?> GetAsync(string tenant, string evidenceId, CancellationToken cancellationToken = default);

    /// <summary>Looks an artifact up by the domain idempotency tuple.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="domainKey">The domain key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The artifact, or <see langword="null"/> when nothing matches.</returns>
    ValueTask<EvidenceArtifact?> FindByDomainKeyAsync(
        string tenant,
        string domainKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an artifact committed.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="evidenceId">The artifact's identity.</param>
    /// <param name="at">When the commit happened.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="false"/> when it was already committed, so a replayed commit is visible rather than
    /// silent.
    /// </returns>
    ValueTask<bool> CommitAsync(
        string tenant,
        string evidenceId,
        string at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends a grant.
    /// </summary>
    /// <remarks>
    /// Returns the grant only if it exists, matches the artifact, is unexpired and unspent. The single-use check
    /// lives here so it is one atomic step rather than a read-then-write race.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="evidenceId">The artifact's identity.</param>
    /// <param name="grantId">The grant's identity.</param>
    /// <param name="now">The clock the expiry is compared against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The grant when it was spent, or <see langword="null"/> when it could not be.</returns>
    ValueTask<EvidenceGrant?> ConsumeGrantAsync(
        string tenant,
        string evidenceId,
        string grantId,
        string now,
        CancellationToken cancellationToken = default);

    /// <summary>Records a resolution.</summary>
    /// <param name="access">The access to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RecordAccessAsync(EvidenceAccess access, CancellationToken cancellationToken = default);

    /// <summary>Reads recent accesses for an artifact, newest first.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="evidenceId">The artifact's identity.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The accesses, newest first.</returns>
    ValueTask<IReadOnlyList<EvidenceAccess>> AccessesAsync(
        string tenant,
        string evidenceId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists committed artifacts whose retention has expired and which are not on legal hold.
    /// </summary>
    /// <remarks>
    /// Across every tenant on purpose: the janitor is a deployment-wide obligation, and a retention policy that
    /// only ran for the tenants somebody remembered to sweep would not be a retention policy.
    /// </remarks>
    /// <param name="now">The clock the expiry is compared against.</param>
    /// <param name="limit">How many to return, oldest expiry first.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The artifacts that are due, oldest expiry first.</returns>
    ValueTask<IReadOnlyList<EvidenceArtifact>> PurgeDueAsync(
        string now,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an artifact purged.
    /// </summary>
    /// <remarks>
    /// Called <em>after</em> the bytes are deleted, and the ordering is chosen for its failure mode:
    /// delete-then-mark can leave a row that still says committed while its bytes are gone - untidy for one
    /// sweep interval, but self-healing, because the next sweep still sees the row as due. Mark-then-delete
    /// would leave an artifact that reports itself purged while its regulated bytes are still on disk, and no
    /// later sweep would ever revisit it. A retention system may be briefly untidy; it may not quietly fail to
    /// delete.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="evidenceId">The artifact's identity.</param>
    /// <param name="at">When the purge happened.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when it was already purged, so two sweeps cannot both claim the row.</returns>
    ValueTask<bool> MarkPurgedAsync(
        string tenant,
        string evidenceId,
        string at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Places or lifts a legal hold.
    /// </summary>
    /// <remarks>
    /// A hold blocks <em>deletion</em>, never <em>reading</em>: it is an instruction to preserve evidence, and an
    /// instruction to preserve something that also hid it would be a strange one. Reads stay governed by the
    /// authorization class exactly as before.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="evidenceId">The artifact's identity.</param>
    /// <param name="hold">Whether to place the hold.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when the artifact is unknown.</returns>
    ValueTask<bool> SetLegalHoldAsync(
        string tenant,
        string evidenceId,
        bool hold,
        CancellationToken cancellationToken = default);
}
