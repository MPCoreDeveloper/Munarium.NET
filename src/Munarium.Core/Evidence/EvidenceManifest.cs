namespace Munarium.Evidence;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The manifest: everything needed to prove what an answer was computed from, decide who may read it, and say
/// honestly whether it can be replayed.
/// </summary>
/// <remarks>
/// The bytes are separate - this names them by <see cref="ArtifactHash"/> and never embeds them - so a manifest
/// can be read, cited and authorized without moving the data it describes.
/// <para>
/// Absent fields mean "not applicable to this kind", never "unknown"; the two are different claims and a reader
/// deciding whether to trust a result needs to know which one it has.
/// </para>
/// </remarks>
public sealed record EvidenceManifest
{
    private const string UnitSeparator = "\u001f";

    /// <summary>Gets the contract version the producer built against.</summary>
    public required string ContractVersion { get; init; }

    /// <summary>Gets the canonicalization version, which is always <c>canon@1</c> in this contract major.</summary>
    public required string Canon { get; init; }

    /// <summary>
    /// Gets the identity the server assigns at seal.
    /// </summary>
    /// <remarks>
    /// Absent in a seal request and present in every read, which is what makes the id a property of the
    /// deployment rather than a claim by the caller.
    /// </remarks>
    public string? EvidenceId { get; init; }

    /// <summary>Gets the tenant the artifact belongs to.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets what the sealed bytes are.</summary>
    public required EvidenceKind Kind { get; init; }

    /// <summary>Gets the hash of the logical result, which is what makes two seals of one answer the same seal.</summary>
    public required string LogicalResultHash { get; init; }

    /// <summary>Gets the hash of the serialized bytes as they were written.</summary>
    public required string ArtifactHash { get; init; }

    /// <summary>Gets how many bytes the artifact holds.</summary>
    public required long BytesLength { get; init; }

    /// <summary>Gets the media type of the bytes.</summary>
    public required string MediaType { get; init; }

    /// <summary>Gets where the result came from.</summary>
    public required SourceRef Source { get; init; }

    /// <summary>Gets the versions of the artifacts that shaped the result.</summary>
    public required Versions Versions { get; init; }

    /// <summary>Gets the plan and parameter hashes, when the result came from a compiled query.</summary>
    public PlanHashes? Plan { get; init; }

    /// <summary>Gets the schema of the result.</summary>
    public required EvidenceSchema Schema { get; init; }

    /// <summary>Gets what makes two rows the same row.</summary>
    public required EvidenceIdentity Identity { get; init; }

    /// <summary>Gets whether the result is complete.</summary>
    public required Completeness Completeness { get; init; }

    /// <summary>Gets what a policy removed before the bytes were written.</summary>
    public Redaction? Redaction { get; init; }

    /// <summary>Gets one marker per source that contributed.</summary>
    public required IReadOnlyList<SnapshotMarker> SnapshotVector { get; init; }

    /// <summary>Gets how current the data behind the result was.</summary>
    public Freshness? Freshness { get; init; }

    /// <summary>Gets the run that produced the result.</summary>
    public required Execution Execution { get; init; }

    /// <summary>Gets the equivalence class the artifact belongs to, and who may read it.</summary>
    public required AuthorizationClass AuthorizationClass { get; init; }

    /// <summary>Gets the retention the artifact was sealed under.</summary>
    public Retention? Retention { get; init; }

    /// <summary>
    /// Computes the domain idempotency tuple: sealing the same logical result, under the same policy, for the
    /// same authorization class, is the same seal.
    /// </summary>
    /// <remarks>
    /// Note what is <em>absent</em>: the artifact hash. Re-serializing one logical result must not mint a
    /// second artifact - that is the whole reason the logical result and the bytes have separate hashes. The
    /// authorization class <em>is</em> present, because the same rows read under two different classes are two
    /// different artifacts as far as who-may-read-this is concerned.
    /// <para>
    /// The fields are joined with a unit separator rather than a comma, including inside the compartment list.
    /// Nothing validates a compartment tag against commas, so <c>["a,b"]</c> and <c>["a", "b"]</c> joined by
    /// commas were one key - which would let a lookup replay an artifact sealed under a different
    /// authorization class. A single-compartment class keys the same as it always did; only multi-compartment
    /// keys moved.
    /// </para>
    /// </remarks>
    /// <returns>The domain key, as <c>dk-</c> plus the SHA-256 of the identity material.</returns>
    public string ComputeDomainKey()
    {
        var compartments = AuthorizationClass.Compartments.Order(StringComparer.Ordinal);

        var material = string.Join(
            UnitSeparator,
            Tenant,
            LogicalResultHash,
            Versions.Policy ?? string.Empty,
            AuthorizationClass.AccessLevel.ToString(CultureInfo.InvariantCulture),
            string.Join(UnitSeparator, compartments));

        return string.Concat("dk-", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))));
    }
}
