namespace Munarium.Evidence;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

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
public sealed record EvidenceManifest : IVerifiableArtifact
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
    /// <remarks>
    /// The wire name is attributed rather than derived, because the contract abbreviates it: a snake-cased property would
    /// put <c>bytes_length</c> on the wire, and the manifest's own JSON schema on disk is the record every other
    /// implementation of this contract reads.
    /// </remarks>
    [JsonPropertyName("bytes_len")]
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

    /// <summary>
    /// Validates the manifest at the door.
    /// </summary>
    /// <remarks>
    /// The producer is strict about what it emits; this is the consumer being strict about what it will later
    /// rely on, so a malformed manifest is refused at seal rather than discovered at resolution - when the
    /// source is long gone and nothing can be repaired.
    /// <para>
    /// The first violation is thrown, not collected, because there is nothing useful a caller can do with the
    /// second one: a manifest has to be re-sealed from a producer either way.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown on the first violation, with the reason in the message.</exception>
    public void Validate()
    {
        if (!string.Equals(Canon, EvidenceContract.Canon, StringComparison.Ordinal))
        {
            throw Invalid(
                $"canon must be '{EvidenceContract.Canon}', got '{Canon}'; this server implements exactly one "
                    + "canonicalization version");
        }

        if (!EvidenceContract.MajorMatches(ContractVersion))
        {
            throw Invalid(
                $"contract major version mismatch: manifest declares '{ContractVersion}', this server was built "
                    + $"against '{EvidenceContract.Version}'. A major bump is a wire break, so the two trees "
                    + "must be deployed together");
        }

        if (Tenant.Trim().Length == 0)
        {
            throw Invalid("tenant is required");
        }

        if (!EvidenceContract.IsHash(LogicalResultHash))
        {
            throw Invalid($"logical_result_hash must be 'sha256:<64 lowercase hex>', got '{LogicalResultHash}'");
        }

        if (!EvidenceContract.IsHash(ArtifactHash))
        {
            throw Invalid($"artifact_hash must be 'sha256:<64 lowercase hex>', got '{ArtifactHash}'");
        }

        if (BytesLength < 0)
        {
            throw Invalid("bytes_len must not be negative");
        }

        if (BytesLength > EvidenceContract.MaxArtifactBytes)
        {
            throw Invalid($"bytes_len {BytesLength} exceeds the {EvidenceContract.MaxArtifactBytes}-byte ceiling");
        }

        if (!IsAcceptedMediaType(MediaType))
        {
            throw Invalid(
                $"media_type must be '{EvidenceContract.MediaTypeParquet}' or '{EvidenceContract.MediaTypeCsv}', "
                    + $"got '{MediaType}'");
        }

        ValidateSchema();
        ValidateSnapshotVector();

        if (AuthorizationClass.AccessLevel < 0)
        {
            throw Invalid("authorization_class.access_level must not be negative");
        }

        ValidateRetention();
        ValidateRowIdentity();
    }

    private void ValidateSchema()
    {
        if (Schema.Columns.Count == 0)
        {
            throw Invalid("schema.columns must not be empty");
        }

        // A duplicate column id would make two columns indistinguishable in a citation, which is the one thing
        // a column id exists to prevent.
        var distinct = Schema.Columns.Select(column => column.Id).Distinct(StringComparer.Ordinal).Count();

        if (distinct != Schema.Columns.Count)
        {
            throw Invalid("schema.columns contains duplicate column ids");
        }
    }

    private void ValidateSnapshotVector()
    {
        if (SnapshotVector.Count == 0)
        {
            throw Invalid(
                "snapshot_vector must name at least one source; an artifact with no snapshot marker cannot "
                    + "state its freshness");
        }
    }

    private void ValidateRetention()
    {
        if (Retention is not { } retention)
        {
            return;
        }

        foreach (var (field, value) in new[]
        {
            ("expires_at", retention.ExpiresAt),
            ("purged_at", retention.PurgedAt),
        })
        {
            if (value is { Length: > 0 } && !IsRfc3339(value))
            {
                throw Invalid($"retention.{field} must be an RFC 3339 timestamp, got '{value}'");
            }
        }
    }

    // The row identity rule has to be satisfiable, which is where it can still be acted on. A result that
    // cannot name its rows cannot be sealed at all: under a positional rule the row ids mean nothing without a
    // total order, so a citation of "row 7" would resolve to whatever row 7 happened to be.
    private void ValidateRowIdentity()
    {
        switch (Identity.RowIdRule)
        {
            case RowIdRule.Position when Identity.OrderBy.Count == 0:
                throw Invalid(
                    "identity.row_id_rule is 'position' but order_by is empty; positional row ids are "
                        + "meaningless without a total ordering");

            case RowIdRule.Keys when !Schema.Columns.Any(column => column.Key):
                throw Invalid(
                    "identity.row_id_rule is 'keys' but no column is marked key; the row id would have "
                        + "nothing to derive from");

            default:
                return;
        }
    }

    // Retention timestamps are compared as text by one store and cast to a timestamp by another, so a value
    // that is not RFC 3339 was an error on one backend and an artifact with undefined retention on the other.
    // Refused at the door instead.
    private static bool IsRfc3339(string value) => DateTimeOffset.TryParseExact(
        value,
        ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out _);

    private static bool IsAcceptedMediaType(string mediaType) =>
        mediaType is EvidenceContract.MediaTypeParquet or EvidenceContract.MediaTypeCsv;

    private static ArgumentException Invalid(string reason) => new(reason);
}
