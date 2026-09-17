namespace Munarium.Claims;

/// <summary>
/// Where a claim that came from a connector originated.
/// </summary>
/// <remarks>
/// A claim proposed by the reconciliation pipeline carries the exact source, row and mapping it was
/// derived from, so a reviewer can walk from a ledger fact back to the sealed evidence without
/// trusting the claim's own wording. It is optional and additive: a claim a model extracted never
/// carries one, and no gate reads it - origin is provenance for people and for the reconciliation
/// pipeline, never an input to acceptance.
/// <para>
/// The timestamps are strings in RFC 3339 form rather than a date type, for the same reason the
/// kernel has no clock: the value then survives every wire - JSON, protobuf, a JSONB column - byte
/// for byte, and a pin over it cannot be perturbed by a time zone.
/// </para>
/// </remarks>
public sealed record ClaimOrigin
{
    /// <summary>Gets the kind of origin: <c>connector</c>, or <c>rollback</c> when a mapping supersedes its own claim.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the source the observation was read from.</summary>
    public required string SourceId { get; init; }

    /// <summary>Gets the <c>name@version</c> of the mapping that produced the claim.</summary>
    public required string MappingVersion { get; init; }

    /// <summary>Gets the source row's stable key, which is the observation's idempotency identity.</summary>
    public required string RowKey { get; init; }

    /// <summary>Gets the engine position the row was read at, when the source exposes one.</summary>
    public string? EventPosition { get; init; }

    /// <summary>Gets when the row was observed, as an RFC 3339 string.</summary>
    public string? ObservedAt { get; init; }

    /// <summary>Gets the sealed evidence artifact the observation batch was sealed as.</summary>
    public string? EvidenceId { get; init; }
}
