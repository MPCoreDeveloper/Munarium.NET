namespace Munarium.Claims;

using System.Globalization;
using Munarium.Facts;
using Munarium.Ledger;

/// <summary>
/// A claim as the kernel reasons over it: the <c>subject.key=value</c> triple that decides what
/// supersedes what, where it sits in the scope tree, and the provenance it was admitted under.
/// </summary>
/// <remarks>
/// The claim key is <see cref="ClaimKey"/> - <c>subject.key</c> - and not the claim's identity: two
/// claims about the same subject and key are about the same thing, which is exactly the test a gate
/// needs when it asks whether a value the ledger already holds is being contradicted. Identity is
/// <see cref="Id"/>, which is what a supersession chain names.
/// <para>
/// <see cref="Sequence"/> is the claim's position in the ledger, so everything downstream - the pin,
/// the digest ladder, the composition order - reads one axis. It is <see cref="SequenceNumber"/>
/// rather than an integer so a claim and a ledger position cannot be silently swapped.
/// </para>
/// </remarks>
public sealed record Claim
{
    /// <summary>Gets the claim's identity, which a supersession names.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the version the claim belongs to.</summary>
    public required string VersionId { get; init; }

    /// <summary>Gets the claim's position in the ledger.</summary>
    public required SequenceNumber Sequence { get; init; }

    /// <summary>Gets what the claim does to whatever the ledger already holds on its claim key.</summary>
    public ClaimType ClaimType { get; init; } = ClaimType.Fact;

    /// <summary>Gets the subject - the thing the claim is about.</summary>
    public required string Subject { get; init; }

    /// <summary>Gets the property of the subject that the claim is about.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the asserted value, as text. Comparison is case- and whitespace-insensitive.</summary>
    public required string Value { get; init; }

    /// <summary>Gets the dotted scope the claim was written in, or <see langword="null"/> for none.</summary>
    public string? ScopePath { get; init; }

    /// <summary>Gets whether the ledger accepted the claim or recorded it as disputed.</summary>
    public ClaimStatus Status { get; init; } = ClaimStatus.Accepted;

    /// <summary>Gets how the claim came to exist. See <see cref="Claims.Provenance"/>.</summary>
    public Provenance Provenance { get; init; } = Provenance.Witnessed;

    /// <summary>
    /// Gets the claim this one supersedes, when it says so explicitly.
    /// </summary>
    /// <remarks>
    /// A claim that names its predecessor is declaring a change, so the conflict gate exempts it -
    /// that is the difference between a correction and a silent overwrite.
    /// </remarks>
    public string? SupersedesId { get; init; }

    /// <summary>Gets the entity the claim was resolved to, when entity resolution ran over it.</summary>
    public string? EntityId { get; init; }

    /// <summary>
    /// Gets the claim's evidence, as JSON text, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Opaque to the kernel: it travels with the claim into the ledger, the digest and the wire, and
    /// nothing here interprets it. Keeping it as text rather than a parsed document is what keeps the
    /// kernel free of a parser it would otherwise have to agree with on every wire.
    /// </remarks>
    public string? EvidenceJson { get; init; }

    /// <summary>Gets the extractor's confidence, when the claim came from a model.</summary>
    public double? Confidence { get; init; }

    /// <summary>Gets the <c>name@version</c> reference of the shape the claim was made under.</summary>
    public string? ShapeRef { get; init; }

    /// <summary>Gets the connector origin, or <see langword="null"/> for a model-extracted claim.</summary>
    public ClaimOrigin? Origin { get; init; }

    /// <summary>Gets the claim key: the identity a supersession chain and the gates key off.</summary>
    public string ClaimKey => string.Concat(Subject, ".", Key);

    /// <summary>Gets the canonical <c>subject.key=value</c> text that every gate parses.</summary>
    public string NormalizedText => ClaimText.Normalize(Subject, Key, Value);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{NormalizedText}@{Sequence.Value}");
}
