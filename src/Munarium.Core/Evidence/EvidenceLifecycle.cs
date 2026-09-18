namespace Munarium.Evidence;

/// <summary>
/// A stored artifact: the manifest plus the server-owned lifecycle facts.
/// </summary>
public sealed record EvidenceArtifact
{
    /// <summary>Gets the identity the server assigned at seal.</summary>
    public required string EvidenceId { get; init; }

    /// <summary>Gets the tenant the artifact belongs to.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the lifecycle state.</summary>
    public required EvidenceState State { get; init; }

    /// <summary>Gets the manifest, which is what proves what the bytes are.</summary>
    public required EvidenceManifest Manifest { get; init; }

    /// <summary>
    /// Gets the blob path under the reserved keyspace.
    /// </summary>
    /// <remarks>
    /// Recorded rather than recomputed so a purge knows exactly what to delete without deriving it again - a
    /// derivation that has to be right at deletion time is a derivation that can be wrong at deletion time.
    /// </remarks>
    public required string BlobPath { get; init; }

    /// <summary>Gets when the row was created.</summary>
    public required string CreatedAt { get; init; }

    /// <summary>Gets when the bytes were committed, or <see langword="null"/> while the artifact is pending.</summary>
    public string? CommittedAt { get; init; }
}

/// <summary>
/// A single-use capability to upload bytes for an artifact the server has already assigned an id to.
/// </summary>
/// <remarks>
/// Single-use is the point: the second attempt is refused even inside the TTL, which is what keeps a leaked
/// grant from being a second write.
/// </remarks>
public sealed record EvidenceGrant
{
    /// <summary>Gets the grant's identity.</summary>
    public required string GrantId { get; init; }

    /// <summary>Gets the artifact the grant is for.</summary>
    public required string EvidenceId { get; init; }

    /// <summary>Gets the tenant.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets when the grant stops being usable.</summary>
    public required string ExpiresAt { get; init; }

    /// <summary>Gets when the grant was spent, or <see langword="null"/> while it is unspent.</summary>
    public string? UsedAt { get; init; }
}

/// <summary>
/// One resolution, recorded.
/// </summary>
/// <remarks>
/// It records <em>that</em> a read happened and by whom - never the rows read. An audit table holding the
/// regulated data it audits is a second copy of the problem the audit exists to describe.
/// </remarks>
public sealed record EvidenceAccess
{
    /// <summary>Gets the artifact that was read.</summary>
    public required string EvidenceId { get; init; }

    /// <summary>Gets the tenant.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets who read it.</summary>
    public required string Uid { get; init; }

    /// <summary>Gets what was read: <c>manifest</c> or <c>rows</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the first row the caller asked for.</summary>
    public long? RowFrom { get; init; }

    /// <summary>Gets how many rows the caller asked for.</summary>
    public long? RowLimit { get; init; }

    /// <summary>Gets the outcome: <c>ok</c>, <c>denied</c>, <c>expired</c> or <c>on-hold</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Gets when the read happened.</summary>
    public required string At { get; init; }
}

/// <summary>
/// What a seal did.
/// </summary>
/// <remarks>
/// Distinguishing a creation from a replay is what lets a caller tell an idempotent retry from a new artifact
/// without comparing identities - and it is why the domain key exists at all.
/// </remarks>
public sealed record SealOutcome
{
    /// <summary>Gets the artifact's identity.</summary>
    public required string EvidenceId { get; init; }

    /// <summary>Gets a value indicating whether this seal created the artifact.</summary>
    public required bool Created { get; init; }

    /// <summary>Gets the upload grant, which is present only on the grant path.</summary>
    public EvidenceGrant? Grant { get; init; }
}
