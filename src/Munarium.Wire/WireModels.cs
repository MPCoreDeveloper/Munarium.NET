namespace Munarium.Wire;

/// <summary>
/// The values the contract's <c>ClaimStatus</c> can carry.
/// </summary>
/// <remarks>
/// Strings rather than a C# enum, because the contract declares ClaimStatus as a string enum and
/// both transports then carry the same spelling without a converter deciding it for them.
/// </remarks>
public static class WireClaimStatus
{
    /// <summary>No status was given.</summary>
    public const string Unspecified = "unspecified";

    /// <summary>Governance permitted the claim; it is recorded as asserted.</summary>
    public const string Accepted = "accepted";

    /// <summary>Governance refused the claim; it is recorded as disputed rather than dropped.</summary>
    public const string Disputed = "disputed";

    /// <summary>The write lost every retry to a moving head. Retryable, never data loss.</summary>
    public const string Contended = "contended";
}

/// <summary>
/// The values the contract's <c>ClaimType</c> can carry: what a claim does to whatever the ledger already
/// holds on its lineage.
/// </summary>
public static class WireClaimTypes
{
    /// <summary>The caller did not say, which is read as a fact rather than as an excuse.</summary>
    public const string Unspecified = "unspecified";

    /// <summary>A value the ledger should not already hold.</summary>
    public const string Fact = "fact";

    /// <summary>The value legitimately changed.</summary>
    public const string Update = "update";

    /// <summary>The earlier value was wrong.</summary>
    public const string Correction = "correction";
}

/// <summary>Liveness.</summary>
/// <param name="Status">Always <c>ok</c> - a health check that answers at all is healthy.</param>
/// <param name="Contract">The wire contract version this server speaks.</param>
public sealed record WireHealth(string Status, string Contract);

/// <summary>The head of a version's stream.</summary>
/// <param name="VersionId">The version that was read.</param>
/// <param name="Head">The last sequence number in it. 0 means nothing has been written.</param>
public sealed record WireVersionHead(string VersionId, long Head);

/// <summary>A claim as proposed, before governance has judged it.</summary>
/// <param name="ClaimId">The claim's identity, unique within the version.</param>
/// <param name="ClaimType">What the claim does to what is already on its lineage.</param>
/// <param name="Shape">The shape the body is validated against and the lineage derived from.</param>
/// <param name="Body">The structured fact body, JSON-encoded.</param>
/// <param name="Statement">The claim in the actor's words.</param>
/// <param name="Actor">Who is asserting it.</param>
public sealed record WireClaimProposal(
    string ClaimId,
    string ClaimType,
    string Shape,
    string Body,
    string Statement,
    string Actor);

/// <summary>A claim as recorded, with the verdict it was recorded under.</summary>
/// <param name="VersionId">The version it was written to.</param>
/// <param name="ClaimId">The claim's identity.</param>
/// <param name="ClaimType">What the claim did to its lineage.</param>
/// <param name="Lineage">Derived by the kernel from the shape's identity fields over the body.</param>
/// <param name="Status">What the ledger recorded.</param>
/// <param name="Gate">The gate that refused it, empty when it was accepted.</param>
/// <param name="Reason">Why it was refused, in the gate's words.</param>
/// <param name="Head">The version's head after the write.</param>
public sealed record WireClaimOutcome(
    string VersionId,
    string ClaimId,
    string ClaimType,
    string Lineage,
    string Status,
    string Gate,
    string Reason,
    long Head);

/// <summary>A machine-actionable failure, mirroring RFC 9457 problem+json.</summary>
/// <param name="Type">A stable problem identifier.</param>
/// <param name="Detail">What happened.</param>
/// <param name="Status">The HTTP status, when the transport has one.</param>
/// <param name="ExpectedHead">Set only when contended - the head the kernel last attempted with.</param>
/// <param name="ActualHead">Set only when contended - the head the store reported.</param>
public sealed record WireProblem(
    string Type,
    string Detail,
    int Status,
    long ExpectedHead,
    long ActualHead);

/// <summary>One fact that was current at a pin.</summary>
/// <param name="VersionId">The version the fact was written to.</param>
/// <param name="ClaimId">The claim's identity.</param>
/// <param name="ClaimType">What the claim did to its lineage.</param>
/// <param name="Lineage">The lineage the fact belongs to.</param>
/// <param name="Statement">The claim in the actor's words.</param>
/// <param name="Actor">Who asserted it.</param>
/// <param name="Status">What the ledger recorded.</param>
/// <param name="Gate">The gate that refused it, empty when it was accepted.</param>
/// <param name="Reason">Why it was refused.</param>
/// <param name="Sequence">The global position that made this fact the current one.</param>
public sealed record WireFact(
    string VersionId,
    string ClaimId,
    string ClaimType,
    string Lineage,
    string Statement,
    string Actor,
    string Status,
    string Gate,
    string Reason,
    long Sequence);

/// <summary>The facts that were current at one pin, and a digest over exactly that state.</summary>
/// <param name="AsOf">The pin this slice was taken at.</param>
/// <param name="Digest">SHA-256 over exactly the facts above, lowercase hex.</param>
/// <param name="Facts">The current facts, ordered by lineage.</param>
public sealed record WireFactSlice(long AsOf, string Digest, IReadOnlyList<WireFact> Facts);

/// <summary>Where one retrieved chunk came from.</summary>
/// <param name="ChunkId">The chunk's stable identity, unique within an index version.</param>
/// <param name="SourceId">The document's identity.</param>
/// <param name="SourcePath">The logical path the document was stored under.</param>
/// <param name="ContentHash">The document's content hash, which proves which bytes it held.</param>
/// <param name="ChunkOrdinal">The chunk's position within its document.</param>
public sealed record WireSourceReference(
    string ChunkId,
    string SourceId,
    string SourcePath,
    string ContentHash,
    int ChunkOrdinal);

/// <summary>The provenance envelope every retrieval answer carries.</summary>
/// <param name="IndexVersion">The immutable index version the answer came from.</param>
/// <param name="LedgerWatermark">The ledger position the index reflects.</param>
/// <param name="Sources">The sources the answer actually used, in answer order.</param>
public sealed record WireProvenanceEnvelope(
    string IndexVersion,
    long LedgerWatermark,
    IReadOnlyList<WireSourceReference> Sources);

/// <summary>One retrieved chunk: its provenance, its fused score, and the text it may use.</summary>
/// <param name="Source">Where the chunk came from.</param>
/// <param name="Score">The fused ranking score.</param>
/// <param name="Text">The chunk text.</param>
public sealed record WireRetrievedChunk(WireSourceReference Source, double Score, string Text);

/// <summary>A retrieval question.</summary>
/// <param name="Text">The question, which drives the lexical leg.</param>
/// <param name="TopK">How many fused chunks the answer may carry.</param>
public sealed record WireSearchQuery(string Text, int TopK);

/// <summary>A retrieval answer.</summary>
/// <param name="Chunks">The retrieved chunks, in fused rank order.</param>
/// <param name="Envelope">The provenance envelope covering exactly those chunks.</param>
public sealed record WireSearchResult(IReadOnlyList<WireRetrievedChunk> Chunks, WireProvenanceEnvelope Envelope);

/// <summary>A versioned, declarative description of what a claim looks like.</summary>
/// <param name="Name">The shape name.</param>
/// <param name="Version">The shape version.</param>
/// <param name="Identity">The body keys that identify a claim lineage.</param>
/// <param name="Schema">The JSON Schema a fact body must satisfy, JSON-encoded.</param>
public sealed record WireShape(string Name, int Version, IReadOnlyList<string> Identity, string Schema);

/// <summary>The shapes a deployment understands.</summary>
/// <param name="Shapes">The registered shapes, ordered by name.</param>
public sealed record WireShapeList(IReadOnlyList<WireShape> Shapes);

/// <summary>A memory version: the identity claims are written under, and the node it occupies.</summary>
/// <param name="VersionId">The version's identity, which is also the stream its claims go to.</param>
/// <param name="ParentVersionId">The version it descends from, empty when it starts a lineage.</param>
/// <param name="AsOfDate">The date it is "as of", empty when it declares none.</param>
/// <param name="Label">The label a human gave it.</param>
/// <param name="Head">The version's own head. 0 means no claim has been written to it yet.</param>
public sealed record WireVersion(
    string VersionId,
    string ParentVersionId,
    string AsOfDate,
    string Label,
    long Head);

/// <summary>The path from a lineage root down to a version, inclusive.</summary>
/// <param name="Versions">The lineage, root first.</param>
public sealed record WireVersionLineage(IReadOnlyList<WireVersion> Versions);

/// <summary>A request to create a version.</summary>
/// <param name="VersionId">The identity to use, or empty to let the server generate one.</param>
/// <param name="ParentVersionId">The version to descend from, empty for a lineage root.</param>
/// <param name="AsOfDate">The date this version is "as of", as YYYY-MM-DD, or empty.</param>
/// <param name="Label">A label for humans, or empty.</param>
/// <param name="Actor">Who is creating it.</param>
public sealed record WireVersionRequest(
    string VersionId,
    string ParentVersionId,
    string AsOfDate,
    string Label,
    string Actor);

/// <summary>One titled block of a composed context.</summary>
/// <param name="Title">The section's title.</param>
/// <param name="Body">The section's text.</param>
public sealed record WireContextSection(string Title, string Body);

/// <summary>A request to compose the context a model would be given.</summary>
/// <param name="VersionId">The version to compose from, or empty for every version.</param>
/// <param name="Shape">The shape to compose from, or empty for every shape.</param>
/// <param name="AsOf">The global position to compose as of. 0 means the present.</param>
/// <param name="AsOfDate">A date to resolve to a pin through the versions, or empty.</param>
/// <param name="BudgetTokens">The token budget for facts. 0 means unbounded.</param>
/// <param name="FactLimit">How many facts may be included. 0 means unbounded.</param>
public sealed record WireContextRequest(
    string VersionId,
    string Shape,
    long AsOf,
    string AsOfDate,
    int BudgetTokens,
    int FactLimit);

/// <summary>
/// The context that was composed: its sections, its text, what it costs to send, and what it covers.
/// </summary>
/// <param name="Sections">The sections, in composition order.</param>
/// <param name="Text">The composed text.</param>
/// <param name="EstimatedTokens">The token estimate for the text.</param>
/// <param name="ContentHash">SHA-256 over the text, lowercase hex.</param>
/// <param name="AsOf">The pin it was composed at.</param>
public sealed record WireComposedContext(
    IReadOnlyList<WireContextSection> Sections,
    string Text,
    int EstimatedTokens,
    string ContentHash,
    long AsOf);

/// <summary>
/// The result of proposing a claim: either the claim as recorded, or a contended write.
/// </summary>
/// <remarks>
/// A union rather than an exception, so a transport cannot quietly turn contention into a 500. REST
/// answers <c>409</c> with the problem and gRPC answers <c>ABORTED</c>, and both have to handle it
/// because forgetting a case does not compile.
/// </remarks>
public readonly union WireClaimResult(WireClaimOutcome, WireProblem);

/// <summary>The result of creating a version: the version, or why it could not be created.</summary>
public readonly union WireVersionResult(WireVersion, WireProblem);

/// <summary>The result of composing a context: the context, or why it could not be composed.</summary>
public readonly union WireContextResult(WireComposedContext, WireProblem);
