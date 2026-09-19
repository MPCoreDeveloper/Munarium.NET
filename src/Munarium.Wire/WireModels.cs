namespace Munarium.Wire;

using Munarium.Evidence;

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
/// <param name="IdempotencyKey">
/// The key this command is made under, or empty for none. The same key on this operation and version is answered with
/// what the first attempt was answered, and writes nothing a second time.
/// </param>
public sealed record WireClaimProposal(
    string ClaimId,
    string ClaimType,
    string Shape,
    string Body,
    string Statement,
    string Actor,
    string? IdempotencyKey = null);

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
/// <param name="IdempotencyKey">
/// The key this command is made under, or empty for none: a retry under one key is answered rather than creating a
/// second version.
/// </param>
public sealed record WireVersionRequest(
    string VersionId,
    string ParentVersionId,
    string AsOfDate,
    string Label,
    string Actor,
    string? IdempotencyKey = null);

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
/// <summary>
/// The values the contract's <c>Provenance</c> can carry: how a claim came to exist.
/// </summary>
public static class WireProvenances
{
    /// <summary>The caller did not say, which is read as a witnessed claim rather than as an excuse.</summary>
    public const string Unspecified = "unspecified";

    /// <summary>Someone or something observed it.</summary>
    public const string Witnessed = "witnessed";

    /// <summary>It was imported from an earlier system rather than observed now.</summary>
    public const string Backfilled = "backfilled";

    /// <summary>It was re-asserted to fix a recorded value.</summary>
    public const string Repaired = "repaired";

    /// <summary>It was concluded from what was observed.</summary>
    public const string Emergent = "emergent";

    /// <summary>It was produced by coverage repair rather than by a witness.</summary>
    public const string CoverageRepair = "coverage_repair";
}

/// <summary>
/// The values the contract's <c>Severity</c> can carry.
/// </summary>
/// <remarks>
/// Strings, not the ledger's numbers: the ledger encodes a severity numerically because its member names are
/// free to change, while the wire is where the names <em>are</em> the contract - a caller reading "warn" learns
/// something a caller reading "1" does not.
/// </remarks>
public static class WireSeverities
{
    /// <summary>No severity was given.</summary>
    public const string Unspecified = "unspecified";

    /// <summary>Worth knowing.</summary>
    public const string Info = "info";

    /// <summary>Worth acting on, and not a refusal.</summary>
    public const string Warn = "warn";

    /// <summary>The claim was refused and recorded as disputed.</summary>
    public const string Block = "block";
}

/// <summary>One claim as the candidate plane sees it: a semantic triple rather than a shaped body.</summary>
/// <param name="ClaimType">What the claim does to whatever the ledger holds on its key.</param>
/// <param name="Subject">The thing the claim is about.</param>
/// <param name="Key">The property of the subject.</param>
/// <param name="Value">The asserted value, as text.</param>
/// <param name="ScopePath">The dotted scope it was produced in, or empty.</param>
/// <param name="Provenance">How the claim came to exist.</param>
/// <param name="SupersedesId">The claim it says it supersedes, or empty.</param>
public sealed record WireClaimCandidate(
    string ClaimType,
    string Subject,
    string Key,
    string Value,
    string ScopePath,
    string Provenance,
    string SupersedesId);

/// <summary>A batch of claims as proposed, with the text the text-shaped gates read.</summary>
/// <param name="Claims">The proposals, in the order they were produced.</param>
/// <param name="Text">The unit of text the text-shaped gates judge, or empty.</param>
/// <param name="ExpectedHead">The head the caller requires, or 0 to append at whatever the head is.</param>
/// <param name="IdempotencyKey">
/// The key this command is made under, or empty for none: a batch is a unit, so a retry of one must not judge it twice.
/// </param>
public sealed record WireClaimBatchRequest(
    IReadOnlyList<WireClaimCandidate> Claims,
    string Text,
    long ExpectedHead,
    string? IdempotencyKey = null);

/// <summary>One thing a gate had to say about a batch.</summary>
/// <param name="RuleId">The dotted rule identifier.</param>
/// <param name="Severity">How serious it is; only <c>block</c> refuses a claim.</param>
/// <param name="Message">The finding in the operator's words.</param>
/// <param name="ScopePath">The scope the batch was judged in, or empty.</param>
/// <param name="ClaimKey">The claim it disputes, or empty when it names none.</param>
/// <param name="Detail">The structured detail as JSON text, carried verbatim.</param>
public sealed record WireFinding(
    string RuleId,
    string Severity,
    string Message,
    string ScopePath,
    string ClaimKey,
    string Detail);

/// <summary>A batch as recorded, with the verdicts it was recorded under.</summary>
/// <param name="VersionId">The version it was written to.</param>
/// <param name="Head">The version's head after the append.</param>
/// <param name="Claims">The claims as recorded, in the order they were proposed.</param>
/// <param name="Findings">Every finding the gates produced, including the ones that refused nothing.</param>
/// <param name="FindingsSequence">The position the findings were recorded at, or 0 when the write produced none.</param>
public sealed record WireClaimBatchOutcome(
    string VersionId,
    long Head,
    IReadOnlyList<WireClaimOutcome> Claims,
    IReadOnlyList<WireFinding> Findings,
    long FindingsSequence);

/// <summary>A finding as recorded, with the position its write settled at.</summary>
/// <param name="Sequence">The position the write settled at.</param>
/// <param name="Finding">The finding itself.</param>
public sealed record WireStoredFinding(long Sequence, WireFinding Finding);

/// <summary>The findings a version's writes produced, oldest first.</summary>
/// <param name="Findings">The findings, oldest first.</param>
public sealed record WireFindingList(IReadOnlyList<WireStoredFinding> Findings);

/// <summary>The values the contract's <c>AnchorStatus</c> can carry.</summary>
public static class WireAnchorStatuses
{
    /// <summary>No status was given.</summary>
    public const string Unspecified = "unspecified";

    /// <summary>The lock still holds, and is the only state judged against.</summary>
    public const string Locked = "locked";

    /// <summary>The detail was released and may drift again.</summary>
    public const string Released = "released";
}

/// <summary>The values the contract's <c>PromiseStatus</c> can carry.</summary>
public static class WirePromiseStatuses
{
    /// <summary>No status was given.</summary>
    public const string Unspecified = "unspecified";

    /// <summary>Nothing has settled it yet.</summary>
    public const string Open = "open";

    /// <summary>It was fulfilled, at the position the promise carries.</summary>
    public const string Fulfilled = "fulfilled";

    /// <summary>Its deadline passed without fulfilment.</summary>
    public const string Expired = "expired";

    /// <summary>It was broken outright.</summary>
    public const string Violated = "violated";
}

/// <summary>Everything the mesh holds at one pin, across every plane.</summary>
/// <param name="VersionId">The version the snapshot was taken of, or empty for every version.</param>
/// <param name="AsOfSequence">The position it was pinned at, or 0 for the present.</param>
/// <param name="AsOfDate">The calendar date it was read as of, or empty.</param>
/// <param name="WrittenAt">The instant the newest identity in it was created at, or empty.</param>
/// <param name="WrittenOn">That instant as a calendar date, or empty.</param>
/// <param name="Facts">The current facts at the pin, in ascending sequence order.</param>
/// <param name="Anchors">The locked anchors, later version winning.</param>
/// <param name="Digests">The digest ladder, rebuilt at the pin.</param>
/// <param name="Promises">The promises, with their status as of the pin.</param>
/// <param name="Counters">The whole-document counters.</param>
/// <param name="Entities">The resolved entities.</param>
public sealed record WireSnapshot(
    string VersionId,
    long AsOfSequence,
    string AsOfDate,
    string WrittenAt,
    string WrittenOn,
    IReadOnlyList<WireResolvedClaim> Facts,
    IReadOnlyList<WireAnchor> Anchors,
    IReadOnlyList<WireDigest> Digests,
    IReadOnlyList<WirePromise> Promises,
    IReadOnlyList<WireCounter> Counters,
    IReadOnlyList<WireEntity> Entities);

/// <summary>A claim as the kernel reasons over it, resolved-current at the pin.</summary>
/// <param name="ClaimId">The claim's identity.</param>
/// <param name="VersionId">The version it was written to.</param>
/// <param name="Sequence">Its position in the ledger as a whole, which is the axis the pin is on.</param>
/// <param name="ClaimType">What it did to whatever the ledger held on its lineage.</param>
/// <param name="Subject">The thing it is about.</param>
/// <param name="Key">The property of the subject.</param>
/// <param name="Value">The asserted value, as text.</param>
/// <param name="ScopePath">The dotted scope it was written in, or empty.</param>
/// <param name="Status">What the ledger recorded.</param>
/// <param name="Provenance">How it came to exist.</param>
/// <param name="SupersedesId">The claim it says it supersedes, or empty.</param>
public sealed record WireResolvedClaim(
    string ClaimId,
    string VersionId,
    long Sequence,
    string ClaimType,
    string Subject,
    string Key,
    string Value,
    string ScopePath,
    string Status,
    string Provenance,
    string SupersedesId);

/// <summary>One rung of the digest ladder.</summary>
/// <param name="VersionId">The version the rung was built for.</param>
/// <param name="Tier">0 per scope, 1 per scope-prefix group, 2 the rollup.</param>
/// <param name="ScopePath">The scope it covers - the group name at tier 1, empty for the rollup.</param>
/// <param name="Content">The rung's content.</param>
/// <param name="ContentHash">The SHA-256 of the content, as lowercase hex.</param>
/// <param name="BuiltFromSequence">The highest ledger position it was built from.</param>
public sealed record WireDigest(
    string VersionId,
    int Tier,
    string ScopePath,
    string Content,
    string ContentHash,
    long BuiltFromSequence);

/// <summary>A locked detail: the detail key may not drift from the locked value while it holds.</summary>
/// <param name="AnchorId">The anchor's identity.</param>
/// <param name="VersionId">The version the lock was taken in.</param>
/// <param name="DetailKey">The locked detail, as <c>subject.key</c>.</param>
/// <param name="LockedValue">The value the detail is pinned to.</param>
/// <param name="LockedAtScope">The scope the lock was taken at, or empty.</param>
/// <param name="Status">Whether the lock still holds.</param>
/// <param name="Sequence">The position the lock was taken at.</param>
/// <param name="Evidence">The evidence the lock was taken on, as JSON text, or empty.</param>
public sealed record WireAnchor(
    string AnchorId,
    string VersionId,
    string DetailKey,
    string LockedValue,
    string LockedAtScope,
    string Status,
    long Sequence,
    string Evidence);

/// <summary>A promise made in one scope and owed to a later one.</summary>
/// <param name="PromiseId">The promise's identity.</param>
/// <param name="VersionId">The version it was made in.</param>
/// <param name="Key">The key it is known by.</param>
/// <param name="Kind">What kind of promise it is.</param>
/// <param name="Description">The promise in words.</param>
/// <param name="OriginScope">The scope it was made in, or empty.</param>
/// <param name="DueScope">The scope it is owed to, or empty.</param>
/// <param name="Status">What became of it, as of the pin.</param>
/// <param name="Sequence">The position it was made at.</param>
/// <param name="FulfilledSequence">The position it was fulfilled at, or 0 while it is open.</param>
public sealed record WirePromise(
    string PromiseId,
    string VersionId,
    string Key,
    string Kind,
    string Description,
    string OriginScope,
    string DueScope,
    string Status,
    long Sequence,
    long FulfilledSequence);

/// <summary>A whole-document frequency, with the ceiling it was declared under.</summary>
/// <param name="Key">The counter's key.</param>
/// <param name="Total">How many times the key has been counted.</param>
/// <param name="Budget">The declared ceiling, or 0 when the counter has none.</param>
/// <param name="OverBudget">Whether the total has passed the ceiling.</param>
public sealed record WireCounter(string Key, long Total, long Budget, bool OverBudget);

/// <summary>A resolved entity, and the aliases it was resolved from.</summary>
/// <param name="EntityId">The entity's identity.</param>
/// <param name="VersionId">The version it was resolved in.</param>
/// <param name="CanonicalName">The name it is known by.</param>
/// <param name="EntityType">Its type, or empty.</param>
/// <param name="Aliases">The names it was resolved from.</param>
/// <param name="Sequence">The position it was resolved at.</param>
/// <param name="MergedInto">The entity it was merged into, or empty.</param>
public sealed record WireEntity(
    string EntityId,
    string VersionId,
    string CanonicalName,
    string EntityType,
    IReadOnlyList<string> Aliases,
    long Sequence,
    string MergedInto);

/// <summary>A whole-document total, as a writer reports it.</summary>
/// <param name="Key">The pattern the counter counts.</param>
/// <param name="Total">The total as it stands, absolute rather than a delta.</param>
/// <param name="Budget">The ceiling the writer is working under, or 0 for none.</param>
/// <param name="IdempotencyKey">
/// The key this command is made under, or empty for none: a retry is answered with the total that landed rather than
/// with a second one.
/// </param>
public sealed record WireCounterRecording(
    string Key,
    long Total,
    long Budget,
    string? IdempotencyKey = null);

/// <summary>The counters at a pin, and the directives that follow from them.</summary>
/// <param name="Counters">The counters at the pin.</param>
/// <param name="Directives">One line per budgeted counter, or empty when none has a budget.</param>
public sealed record WireCounterList(IReadOnlyList<WireCounter> Counters, string Directives);

/// <summary>The result of recording a counter: the counter as recorded, or why it was not.</summary>
public readonly union WireCounterResult(WireCounter, WireProblem);

/// <summary>A lock as the caller asks for it: the detail key is derived from subject and key.</summary>
/// <param name="Subject">The thing whose detail is locked.</param>
/// <param name="Key">The property of the subject.</param>
/// <param name="Value">The value the detail is pinned to.</param>
/// <param name="ScopePath">The scope the lock is taken at, or empty.</param>
/// <param name="Evidence">The evidence the lock is taken on, as JSON text, or empty.</param>
/// <param name="IdempotencyKey">
/// The key this command is made under, or empty for none: a retry with a different value is answered with the value that
/// was locked rather than re-locking it.
/// </param>
public sealed record WireAnchorLock(
    string Subject,
    string Key,
    string Value,
    string ScopePath,
    string Evidence,
    string? IdempotencyKey = null);

/// <summary>A promise as the caller asks for it.</summary>
/// <param name="Key">The coordination key.</param>
/// <param name="Kind">What kind of promise it is.</param>
/// <param name="Description">The promise in words.</param>
/// <param name="OriginScope">The scope it is made in, or empty.</param>
/// <param name="DueScope">The scope it is owed to, or empty.</param>
/// <param name="IdempotencyKey">
/// The key this command is made under, or empty for none: a retry opens one promise rather than two.
/// </param>
public sealed record WirePromiseRegistration(
    string Key,
    string Kind,
    string Description,
    string OriginScope,
    string DueScope,
    string? IdempotencyKey = null);

/// <summary>Whether a lock was released.</summary>
/// <param name="Released">False when nothing was locked, in which case nothing was written either.</param>
public sealed record WireAnchorRelease(bool Released);

/// <summary>Whether a promise was fulfilled.</summary>
/// <param name="Fulfilled">False when no promise with that key was open.</param>
public sealed record WirePromiseFulfilment(bool Fulfilled);

/// <summary>The locked details as they stand at a pin.</summary>
/// <param name="Anchors">The locks, later version winning and released ones absent.</param>
public sealed record WireAnchorList(IReadOnlyList<WireAnchor> Anchors);

/// <summary>The promises as they stand at a pin, and the overdue findings when they were asked for.</summary>
/// <param name="Promises">The promises at the pin.</param>
/// <param name="Findings">The promise check's findings, or empty when the read did not ask for them.</param>
public sealed record WirePromiseList(
    IReadOnlyList<WirePromise> Promises,
    IReadOnlyList<WireFinding> Findings);

/// <summary>The result of locking a detail: the lock as recorded, or why it was not.</summary>
public readonly union WireAnchorResult(WireAnchor, WireProblem);

/// <summary>The result of releasing a lock: whether anything was released, or why nothing was.</summary>
public readonly union WireReleaseResult(WireAnchorRelease, WireProblem);

/// <summary>The result of registering a promise: the promise as recorded, or why it was not.</summary>
public readonly union WirePromiseResult(WirePromise, WireProblem);

/// <summary>The result of fulfilling a promise: whether anything was fulfilled, or why nothing was.</summary>
public readonly union WireFulfilResult(WirePromiseFulfilment, WireProblem);

public readonly union WireClaimResult(WireClaimOutcome, WireProblem);

/// <summary>The result of creating a version: the version, or why it could not be created.</summary>
public readonly union WireVersionResult(WireVersion, WireProblem);

/// <summary>The result of composing a context: the context, or why it could not be composed.</summary>
public readonly union WireContextResult(WireComposedContext, WireProblem);

/// <summary>The result of proposing a batch: the batch as recorded, or why it was not.</summary>
public readonly union WireClaimBatchResult(WireClaimBatchOutcome, WireProblem);

/// <summary>
/// The values the contract's source ingest kinds can carry.
/// </summary>
/// <remarks>
/// Strings rather than a C# enum, for the same reason the claim statuses are: the contract declares them as strings and
/// both transports then carry the same spelling without a converter deciding it for them.
/// </remarks>
public static class WireSourceKinds
{
    /// <summary>The path was not a source before.</summary>
    public const string New = "new";

    /// <summary>The path was a source and now holds different bytes.</summary>
    public const string Replaced = "replaced";

    /// <summary>The path already held exactly these bytes, so nothing was written.</summary>
    public const string Unchanged = "unchanged";
}

/// <summary>A document as it is offered for ingest.</summary>
/// <param name="Path">The logical path, which is the source's identity.</param>
/// <param name="MediaType">The media type of the text, with or without parameters.</param>
/// <param name="Content">The document's text, which is read as UTF-8.</param>
/// <param name="ContentSha256">The hash the caller declares, or empty to have the server hash what arrived.</param>
public sealed record WireSourceIngest(
    string Path,
    string MediaType,
    string Content,
    string? ContentSha256);

/// <summary>A document as it was stored, and how much of it reached the index.</summary>
/// <param name="SourceId">The source's identity, derived from the tenant and the path.</param>
/// <param name="Path">The logical path.</param>
/// <param name="Kind">Whether the path was new, replaced, or already held these bytes.</param>
/// <param name="MediaType">The media type the bytes were stored as.</param>
/// <param name="ContentHash">The hash of the bytes stored.</param>
/// <param name="Bytes">How many bytes of UTF-8 that is.</param>
/// <param name="BlobUri">Where the bytes went, as the backend resolved it.</param>
/// <param name="BackendId">The backend that holds them.</param>
/// <param name="IngestedAt">When the row was written, as the backend recorded it.</param>
/// <param name="ChunksIndexed">How many chunks were written into the index.</param>
/// <param name="IndexVersion">The index version they were written into.</param>
public sealed record WireIngestedSource(
    string SourceId,
    string Path,
    string Kind,
    string MediaType,
    string ContentHash,
    long Bytes,
    string BlobUri,
    string BackendId,
    string? IngestedAt,
    int ChunksIndexed,
    string IndexVersion);

/// <summary>Where a document actually went: metadata, never the bytes.</summary>
/// <param name="SourceId">The source's identity.</param>
/// <param name="Path">The logical path.</param>
/// <param name="MediaType">The media type the bytes were stored as.</param>
/// <param name="ContentHash">The hash of the bytes the path holds now.</param>
/// <param name="Bytes">How many bytes that is.</param>
/// <param name="BlobUri">Where the bytes went.</param>
/// <param name="BackendId">The backend that holds them.</param>
/// <param name="IngestedAt">When the row was last written.</param>
public sealed record WireSourceInfo(
    string SourceId,
    string Path,
    string MediaType,
    string ContentHash,
    long Bytes,
    string BlobUri,
    string BackendId,
    string? IngestedAt);

/// <summary>The result of ingesting a document: what was stored and indexed, or why nothing was.</summary>
public readonly union WireIngestResult(WireIngestedSource, WireProblem);

/// <summary>The result of reading a source's row: the row, or why there is none.</summary>
public readonly union WireSourceResult(WireSourceInfo, WireProblem);

/// <summary>The sources a deployment holds, optionally under one prefix.</summary>
/// <param name="Sources">The rows, in path order.</param>
public sealed record WireSourceList(IReadOnlyList<WireSourceInfo> Sources);

/// <summary>A collection's versions, live and superseded.</summary>
/// <param name="CollectionId">The collection that was asked about.</param>
/// <param name="Versions">The versions, by the position they were built against, latest first.</param>
public sealed record WireIndexVersionList(string CollectionId, IReadOnlyList<WireIndexVersion> Versions);


/// <summary>
/// What an index version was built from, as an operator reads it.
/// </summary>
/// <remarks>
/// Every field here is identity material - the embedder, the chunker, the extractors and the engine all change what a
/// query would match - which is why a manifest is read as the explanation of a version rather than as a description of
/// one.
/// </remarks>
/// <param name="CollectionId">The collection the version indexes.</param>
/// <param name="CollectionName">The collection's name.</param>
/// <param name="ShapeRef">The contract shape the corpus was mapped through.</param>
/// <param name="Engine">The retrieval engine that built the vectors, as a versioned reference.</param>
/// <param name="Chunker">The chunker's version.</param>
/// <param name="Extractors">The extractors' version.</param>
/// <param name="Embedder">The embedder's fingerprint.</param>
/// <param name="MaxChars">The character ceiling a chunk was cut at.</param>
/// <param name="SourceContentHashes">The hash of every source the version indexed.</param>
public sealed record WireIndexManifest(
    string CollectionId,
    string CollectionName,
    string ShapeRef,
    string Engine,
    string Chunker,
    string Extractors,
    string Embedder,
    int MaxChars,
    IReadOnlyList<string> SourceContentHashes);

/// <summary>An index version, as it stands.</summary>
/// <param name="IndexVersionId">The version's identity.</param>
/// <param name="CollectionId">The collection it belongs to.</param>
/// <param name="ShapeRef">The shape it answers for.</param>
/// <param name="Watermark">The ledger position it was built against.</param>
/// <param name="Active">Whether it is the collection's live version.</param>
/// <param name="Superseded">Whether it was once live and is not now.</param>
/// <param name="ActivatedAt">When it first went live.</param>
/// <param name="DeactivatedAt">When it stopped being live.</param>
/// <param name="Manifest">What it was built from.</param>
public sealed record WireIndexVersion(
    string IndexVersionId,
    string CollectionId,
    string ShapeRef,
    long Watermark,
    bool Active,
    bool Superseded,
    string? ActivatedAt,
    string? DeactivatedAt,
    WireIndexManifest Manifest);

/// <summary>A build to make: which collection, over which sources, and whether it should serve.</summary>
/// <param name="CollectionId">The collection the version belongs to.</param>
/// <param name="CollectionName">The collection's name, so a manifest needs no second lookup to be read.</param>
/// <param name="ShapeRef">The contract shape the corpus is mapped through.</param>
/// <param name="PathPrefix">
/// The path prefix that binds the collection, or empty for every source the deployment has.
/// </param>
/// <param name="Activate">Whether the built version becomes the collection's live one.</param>
public sealed record WireIndexBuild(
    string CollectionId,
    string CollectionName,
    string ShapeRef,
    string PathPrefix,
    bool Activate);

/// <summary>What to cut a collection over to.</summary>
/// <param name="CollectionId">The collection to cut over; a version can only be activated into its own collection.</param>
public sealed record WireIndexActivation(string CollectionId);

/// <summary>An answer's provenance envelope, as it is offered for resolution.</summary>
/// <param name="IndexVersion">The version the answer named.</param>
/// <param name="LedgerWatermark">The ledger position the answer's index reflected.</param>
/// <param name="Sources">The sources the answer cited.</param>
public sealed record WireEnvelopeQuery(
    string IndexVersion,
    long LedgerWatermark,
    IReadOnlyList<WireSourceReference> Sources);

/// <summary>
/// What an envelope resolves to, and whether the answer's provenance holds up.
/// </summary>
/// <param name="Resolved">Whether the version exists and the cited bytes are in it.</param>
/// <param name="Failure">Why it does not hold up, or empty when it does.</param>
/// <param name="Version">The version it names, or empty when there is none.</param>
/// <param name="UnrecordedContentHashes">The cited hashes the version's manifest does not record.</param>
public sealed record WireEnvelopeResolution(
    bool Resolved,
    string? Failure,
    WireIndexVersion? Version,
    IReadOnlyList<string> UnrecordedContentHashes);

/// <summary>The result of a build: the version as recorded, or why nothing was built.</summary>
public readonly union WireIndexBuildResult(WireIndexVersion, WireProblem);

/// <summary>The result of reading or cutting over a version: the version, or why there is none.</summary>
public readonly union WireIndexResult(WireIndexVersion, WireProblem);

/// <summary>The result of resolving an envelope: the resolution, or why it could not be attempted.</summary>
public readonly union WireEnvelopeResult(WireEnvelopeResolution, WireProblem);

/// <summary>A manifest offered for sealing, with the bytes when they are small enough to travel with it.</summary>
/// <param name="Manifest">The manifest as the sealer derived it.</param>
/// <param name="BytesBase64">
/// The canonical bytes, base64, or empty to take an upload grant instead - which is what a set of bytes too large to
/// travel inline has to do.
/// </param>
public sealed record WireSealEvidenceRequest(EvidenceManifest Manifest, string? BytesBase64 = null);

/// <summary>What a seal did.</summary>
/// <param name="EvidenceId">The identity the server assigned.</param>
/// <param name="State">The artifact's state, which is the created artifact's and not a constant.</param>
/// <param name="Created">Whether this call created the artifact rather than finding the seal already made.</param>
/// <param name="Grant">The upload grant, present only on the grant path.</param>
public sealed record WireSealResponse(
    string EvidenceId,
    string State,
    bool Created,
    WireEvidenceGrant? Grant = null);

/// <summary>A single-use capability to upload an artifact's bytes.</summary>
/// <param name="GrantId">The grant's identity.</param>
/// <param name="ExpiresAt">When it stops being usable.</param>
public sealed record WireEvidenceGrant(string GrantId, string ExpiresAt);

/// <summary>One resolution, as the audit reports it.</summary>
/// <param name="Uid">Who read it.</param>
/// <param name="Kind">What was read: <c>manifest</c> or <c>rows</c>.</param>
/// <param name="RowFrom">The first row the caller asked for, or none.</param>
/// <param name="RowLimit">How many rows the caller asked for, or none.</param>
/// <param name="Outcome">How it went: <c>ok</c>, <c>denied</c>, <c>expired</c> or <c>on-hold</c>.</param>
/// <param name="At">When the read happened.</param>
public sealed record WireEvidenceAccess(
    string Uid,
    string Kind,
    long? RowFrom,
    long? RowLimit,
    string Outcome,
    string At);

/// <summary>An artifact's resolutions, newest first.</summary>
/// <param name="EvidenceId">The artifact they resolved.</param>
/// <param name="Accesses">The resolutions.</param>
public sealed record WireEvidenceAccessList(string EvidenceId, IReadOnlyList<WireEvidenceAccess> Accesses);

/// <summary>A page of an artifact's rows, each keyed by the column names the manifest declares.</summary>
/// <param name="EvidenceId">The artifact the rows belong to.</param>
/// <param name="From">The first row of this page.</param>
/// <param name="HasMore">Whether there are rows after this page.</param>
/// <param name="Rows">The page.</param>
/// <param name="Total">How many rows the artifact holds in all.</param>
public sealed record WireEvidenceRows(
    string EvidenceId,
    long From,
    bool HasMore,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
    long Total);

/// <summary>What a commit did.</summary>
/// <param name="EvidenceId">The artifact.</param>
/// <param name="State">The state it is in now.</param>
/// <param name="Committed">Whether this call committed it rather than finding it committed already.</param>
public sealed record WireEvidenceCommit(string EvidenceId, string State, bool Committed);

/// <summary>What a purge did.</summary>
/// <param name="EvidenceId">The artifact.</param>
/// <param name="Purged">Whether this call purged it rather than finding it purged already.</param>
/// <param name="State">The state it is in now, which is purged either way.</param>
public sealed record WireEvidencePurge(string EvidenceId, bool Purged, string State);

/// <summary>The bytes of an artifact, on their way up.</summary>
/// <param name="BytesBase64">
/// The bytes, base64. Base64 rather than the octet stream the original takes, because this port's surfaces are JSON by
/// design and the canonical form includes Parquet, which is not text.
/// </param>
public sealed record WireEvidenceBytesUpload(string BytesBase64);

/// <summary>Whether an artifact is held.</summary>
/// <param name="Hold">Whether to place the hold, or lift it.</param>
public sealed record WireEvidenceLegalHold(bool Hold);

/// <summary>The result of a seal: the artifact as recorded, or why nothing was.</summary>
public readonly union WireSealResult(WireSealResponse, WireProblem);

/// <summary>The result of reading a manifest: the manifest itself, or why it does not resolve.</summary>
public readonly union WireEvidenceManifestResult(EvidenceManifest, WireProblem);

/// <summary>The result of reading rows: the page, or why the artifact does not resolve.</summary>
public readonly union WireEvidenceRowsResult(WireEvidenceRows, WireProblem);

/// <summary>The result of reading the audit: the resolutions, or why they may not be read.</summary>
public readonly union WireEvidenceAccessResult(WireEvidenceAccessList, WireProblem);

/// <summary>The result of committing: what happened, or why it did not.</summary>
public readonly union WireEvidenceCommitResult(WireEvidenceCommit, WireProblem);

/// <summary>The result of purging: what happened, or why it did not.</summary>
public readonly union WireEvidencePurgeResult(WireEvidencePurge, WireProblem);



/// <summary>A runbook as offered for application.</summary>
/// <param name="Yaml">The document as written.</param>
public sealed record WireRunbookApply(string Yaml);

/// <summary>A runbook version as applied.</summary>
/// <param name="RunbookRef">The pinned reference, <c>name@version</c>.</param>
/// <param name="Name">The runbook's name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Status">Where it stands: <c>active</c>, <c>remove_requested</c> or <c>removed</c>.</param>
/// <param name="CreatedAt">When this version was first applied.</param>
/// <param name="UpdatedAt">When it was last applied.</param>
public sealed record WireAppliedRunbook(
    string RunbookRef,
    string Name,
    int Version,
    string Status,
    string? CreatedAt = null,
    string? UpdatedAt = null);

/// <summary>One applied runbook version, as listed.</summary>
/// <param name="RunbookRef">The pinned reference, <c>name@version</c>.</param>
/// <param name="Name">The runbook's name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="RemovalId">The removal it is armed with, when one is in flight.</param>
/// <param name="RemovalRequestedAt">When that removal was asked for.</param>
/// <param name="RemovalRequestedBy">Who asked for it.</param>
/// <param name="RemovedAt">When the removal was confirmed.</param>
/// <param name="CreatedAt">When this version was first applied.</param>
/// <param name="UpdatedAt">When it was last applied.</param>
public sealed record WireRunbook(
    string RunbookRef,
    string Name,
    int Version,
    string Status,
    string? RemovalId = null,
    string? RemovalRequestedAt = null,
    string? RemovalRequestedBy = null,
    string? RemovedAt = null,
    string? CreatedAt = null,
    string? UpdatedAt = null);

/// <summary>The applied runbook versions.</summary>
/// <param name="Runbooks">The versions, in name and then version order.</param>
public sealed record WireRunbookList(IReadOnlyList<WireRunbook> Runbooks);

/// <summary>The result of applying a runbook: the version, or why it was not applied.</summary>
public readonly union WireApplyRunbookResult(WireAppliedRunbook, WireProblem);


/// <summary>A session that was opened.</summary>
/// <param name="SessionId">Its identity.</param>
/// <param name="RunbookRef">The version it pinned, <c>name@version</c>.</param>
/// <param name="PermittedCollections">The collections its clearance covers, in the document's order.</param>
public sealed record WireSessionCreated(
    string SessionId,
    string RunbookRef,
    IReadOnlyList<string> PermittedCollections);

/// <summary>What a turn is asked to do.</summary>
/// <param name="Query">The question as asked.</param>
/// <param name="TopK">How many chunks the answer may carry, or absent for the runbook's own.</param>
/// <param name="Complete">Whether the runbook's completion step runs, when it declares one.</param>
/// <param name="ResearchProfile">The profile to run under, or absent for the runbook's default.</param>
public sealed record WireTurnRequest(
    string Query,
    int? TopK = null,
    bool? Complete = null,
    string? ResearchProfile = null);

/// <summary>One hit a turn retrieved.</summary>
/// <param name="ChunkId">The chunk's identity, which is also the label a citation names.</param>
/// <param name="SourcePath">The document it came from.</param>
/// <param name="Score">Its fused score.</param>
/// <param name="Text">Its text.</param>
public sealed record WireTurnHit(string ChunkId, string SourcePath, double Score, string Text);

/// <summary>One source inside a turn's provenance.</summary>
/// <param name="ChunkId">The chunk's identity.</param>
/// <param name="SourcePath">The document's path.</param>
/// <param name="ContentHash">The hash the version indexed.</param>
public sealed record WireTurnSource(string ChunkId, string SourcePath, string ContentHash);

/// <summary>The provenance a turn's hits came with.</summary>
/// <param name="IndexVersion">The version that answered.</param>
/// <param name="LedgerWatermark">The ledger position it reflects.</param>
/// <param name="Sources">The sources the hits name.</param>
public sealed record WireTurnEnvelope(
    string IndexVersion,
    long LedgerWatermark,
    IReadOnlyList<WireTurnSource> Sources);

/// <summary>What a turn's answer was checked against, and what was found.</summary>
/// <param name="Checks">The checks that ran, in the order a report lists them.</param>
/// <param name="Retries">How many corrective completions were paid for.</param>
/// <param name="FirstPassViolations">What the first answer failed, before any correction.</param>
/// <param name="Violations">What the last one failed, or empty when it stands clean.</param>
public sealed record WireTurnVerification(
    IReadOnlyList<string> Checks,
    int Retries,
    IReadOnlyList<string> FirstPassViolations,
    IReadOnlyList<string> Violations);

/// <summary>What a turn's answer cost, and what it was found to be.</summary>
/// <param name="Text">The answer that stands.</param>
/// <param name="InputTokens">Tokens consumed across every call, not just the last.</param>
/// <param name="OutputTokens">Tokens produced across every call.</param>
/// <param name="Completions">How many completions the turn paid for.</param>
/// <param name="RetriedForTruncation">Whether any attempt was retried for stopping early.</param>
/// <param name="Verification">What was checked.</param>
public sealed record WireTurnCompletion(
    string Text,
    int InputTokens,
    int OutputTokens,
    int Completions,
    bool RetriedForTruncation,
    WireTurnVerification Verification);

/// <summary>What one evidence layer produced.</summary>
/// <param name="Layer">Its name.</param>
/// <param name="Role">The weight its evidence carries.</param>
/// <param name="Requirement">Whether its evidence was required.</param>
/// <param name="Block">What it produced: hits, a table, a count, a slice, or a refusal.</param>
/// <param name="EvidenceId">The sealed artifact, when there is one.</param>
/// <param name="SupportsCompleteness">Whether an answer may claim completeness on this layer.</param>
/// <param name="RefusalCode">Why it could not answer, when it could not.</param>
/// <param name="ElapsedMs">How long it took.</param>
public sealed record WireLayerOutcome(
    string Layer,
    string Role,
    string Requirement,
    string Block,
    string? EvidenceId,
    bool SupportsCompleteness,
    string? RefusalCode,
    long ElapsedMs);

/// <summary>Why the model saw what it saw.</summary>
/// <param name="Profile">The profile that ran.</param>
/// <param name="IntentKind">What the question was understood to be asking, when that was modelled.</param>
/// <param name="IntentExplicit">Whether the intent was supplied rather than modelled.</param>
/// <param name="Layers">What each layer produced, in execution order.</param>
/// <param name="CompletenessAvailable">Whether a completeness claim was permissible at all.</param>
/// <param name="DisclosedConflicts">How many conflicts the policy disclosed.</param>
/// <param name="ConflictsPolicy">The policy that decided.</param>
public sealed record WireHierarchyDecision(
    string Profile,
    string? IntentKind,
    bool IntentExplicit,
    IReadOnlyList<WireLayerOutcome> Layers,
    bool CompletenessAvailable,
    int DisclosedConflicts,
    string ConflictsPolicy);

/// <summary>What a turn produced.</summary>
/// <param name="Ordinal">Its position in the conversation, one-based.</param>
/// <param name="Query">The question as asked.</param>
/// <param name="IntentKind">What it was understood to be asking, when that was modelled.</param>
/// <param name="IntentExplicit">Whether the intent was supplied rather than modelled.</param>
/// <param name="CollectionsSearched">The collections it searched, after access filtering.</param>
/// <param name="Hits">The merged hits.</param>
/// <param name="Envelopes">The provenance they came with.</param>
/// <param name="Completion">The answer, when a model answered.</param>
/// <param name="Hierarchy">What the hierarchy decided, when a research profile ran.</param>
public sealed record WireTurnResponse(
    int Ordinal,
    string Query,
    string? IntentKind,
    bool IntentExplicit,
    IReadOnlyList<string> CollectionsSearched,
    IReadOnlyList<WireTurnHit> Hits,
    IReadOnlyList<WireTurnEnvelope> Envelopes,
    WireTurnCompletion? Completion = null,
    WireHierarchyDecision? Hierarchy = null);

/// <summary>One turn as it was recorded, with the payloads as the store kept them.</summary>
/// <param name="Ordinal">Its position in the conversation, one-based.</param>
/// <param name="Query">The question as asked.</param>
/// <param name="CollectionsSearched">The collections it searched.</param>
/// <param name="Hits">The merged hits, as recorded, as JSON text.</param>
/// <param name="Envelope">The provenance, as recorded, as JSON text.</param>
/// <param name="Completion">The completion audit, as recorded, when there was one.</param>
/// <param name="Hierarchy">The hierarchy decision, as recorded, when a profile ran.</param>
/// <param name="CreatedAt">When it was recorded.</param>
public sealed record WireSessionTurn(
    int Ordinal,
    string Query,
    IReadOnlyList<string> CollectionsSearched,
    string Hits,
    string Envelope,
    string? Completion = null,
    string? Hierarchy = null,
    string? CreatedAt = null);

/// <summary>A session and its turns.</summary>
/// <param name="SessionId">Its identity.</param>
/// <param name="Uid">The caller it speaks for.</param>
/// <param name="RunbookRef">The version it pinned.</param>
/// <param name="AccessLevel">The level it snapshotted.</param>
/// <param name="Compartments">The compartments it snapshotted.</param>
/// <param name="State">Where it stands: <c>open</c>, <c>closed</c> or <c>expired</c>.</param>
/// <param name="CreatedAt">When it was opened.</param>
/// <param name="LastTurnAt">When its last turn was recorded.</param>
/// <param name="Turns">Its turns, oldest first.</param>
public sealed record WireSession(
    string SessionId,
    string Uid,
    string RunbookRef,
    int AccessLevel,
    IReadOnlyList<string> Compartments,
    string State,
    string? CreatedAt,
    string? LastTurnAt,
    IReadOnlyList<WireSessionTurn> Turns);

/// <summary>A session that was closed.</summary>
/// <param name="SessionId">Its identity.</param>
/// <param name="State">Where it now stands.</param>
public sealed record WireSessionClosed(string SessionId, string State);

/// <summary>The result of opening a session: the session, or why it was not opened.</summary>
public readonly union WireCreateSessionResult(WireSessionCreated, WireProblem);

/// <summary>
/// One progress event on the streaming turn plane.
/// </summary>
/// <remarks>
/// The contract's vocabulary is flat - one event with a <c>stage</c> discriminator - and the port's split is one record
/// per stage, so an event carries only its own stage's fields and a stage a turn never crossed produces no event at all.
/// <para>
/// Three of the contract's fourteen stages are deliberately absent from this union: <c>probe</c>, <c>selection</c> and
/// <c>retrieval</c> come from a retrieval path that probes collection by collection and searches each one in turn. This
/// port serves one index across every collection a session may read, so it has no such boundary to report - and a record
/// for them would be a shape nothing could ever produce.
/// </para>
/// </remarks>
public readonly union WireTurnEvent(
    WireTurnProfileEvent,
    WireTurnLayerStartEvent,
    WireTurnLayerSourceEvent,
    WireTurnLayerCompleteEvent,
    WireTurnCoverageEvent,
    WireTurnComposeEvent,
    WireTurnModelEvent,
    WireTurnExpansionEvent,
    WireTurnMergeEvent,
    WireTurnCompletionEvent,
    WireTurnVerifyEvent);

/// <summary>
/// What every progress event has in common: it names its own stage.
/// </summary>
/// <remarks>
/// The stage is a property rather than a constructor argument, and both reasons are deliberate. A string a caller could
/// pass would let a merge event go out claiming to be a completion's; and a property that reads no instance state is
/// flagged by the analyzers as one that should be static - which would drop the discriminator the contract requires,
/// because a static member is not serialized at all.
/// </remarks>
public interface IWireTurnEvent
{
    /// <summary>Gets the stage this event reports.</summary>
    string Stage { get; }
}

/// <summary>The profile a turn resolved, before any layer ran.</summary>
/// <param name="Profile">The profile's name.</param>
/// <param name="Layers">The layers that will run, in order.</param>
/// <param name="IntentKind">The intent's kind, when it had one.</param>
/// <param name="IntentExplicit">Whether the intent was supplied rather than modelled.</param>
public sealed record WireTurnProfileEvent(
    string Profile,
    IReadOnlyList<string> Layers,
    string? IntentKind,
    bool IntentExplicit) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "profile";
}

/// <summary>A layer began.</summary>
/// <param name="Layer">The layer's name.</param>
/// <param name="Role">The weight its evidence carries.</param>
/// <param name="Requirement">Whether its evidence is required.</param>
public sealed record WireTurnLayerStartEvent(string Layer, string Role, string Requirement) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "layer_start";
}

/// <summary>A provider was bound to a pinned source.</summary>
/// <param name="Layer">The layer's name.</param>
/// <param name="Source">The pinned source.</param>
/// <param name="Provider">The provider that claimed it.</param>
public sealed record WireTurnLayerSourceEvent(string Layer, string Source, string Provider) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "layer_source";
}

/// <summary>A layer finished, with what it produced.</summary>
/// <param name="Layer">The layer's name.</param>
/// <param name="Block">The block's kind.</param>
/// <param name="SupportsCompleteness">Whether the block permits a completeness claim.</param>
/// <param name="RefusalCode">The refusal's code, when the layer declined.</param>
/// <param name="ElapsedMs">How long the layer took.</param>
public sealed record WireTurnLayerCompleteEvent(
    string Layer,
    string Block,
    bool SupportsCompleteness,
    string? RefusalCode,
    long ElapsedMs) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "layer_complete";
}

/// <summary>What the run's blocks collectively permit, once every layer has run.</summary>
/// <param name="CompletenessAvailable">Whether any block permits a completeness claim.</param>
/// <param name="DisclosedConflicts">How many conflicts between layers will be disclosed.</param>
public sealed record WireTurnCoverageEvent(bool CompletenessAvailable, int DisclosedConflicts) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "coverage";
}

/// <summary>The hierarchy's blocks were composed into the model's context.</summary>
/// <param name="LayersUsed">How many blocks went into the context.</param>
/// <param name="ContextChars">How many characters that context came to.</param>
/// <param name="LayersDropped">The layers that did not fit the budget, dropped whole.</param>
public sealed record WireTurnComposeEvent(int LayersUsed, int ContextChars, IReadOnlyList<string> LayersDropped) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "compose";
}

/// <summary>The model the turn resolved for its paid steps, before any of them was paid for.</summary>
/// <param name="Provider">The provider dialect that will answer.</param>
/// <param name="Model">The model the deployment resolved.</param>
/// <param name="Tier">The tier it resolved through, when it resolved a tier rather than a model.</param>
/// <param name="WasOverride">Whether the caller asked for it rather than the runbook.</param>
public sealed record WireTurnModelEvent(string Provider, string Model, string? Tier, bool WasOverride) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "model";
}

/// <summary>The query was widened by a model, and this is what the call cost.</summary>
/// <param name="Provider">The provider dialect that answered.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="Terms">The accepted variants, in the order the model offered them.</param>
/// <param name="InputTokens">What the call cost to send.</param>
/// <param name="OutputTokens">What the call cost to generate.</param>
public sealed record WireTurnExpansionEvent(
    string Provider,
    string Model,
    IReadOnlyList<string> Terms,
    int InputTokens,
    int OutputTokens) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "expansion";
}

/// <summary>The retrieval the turn's evidence came from returned.</summary>
/// <param name="Hits">How many chunks the merged result carried.</param>
public sealed record WireTurnMergeEvent(int Hits) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "merge";
}

/// <summary>One paid completion returned.</summary>
/// <param name="Attempt">Zero for the answer and its truncation re-ask, one upward per corrective retry.</param>
/// <param name="Provider">The provider dialect that answered.</param>
/// <param name="Model">The model that actually answered.</param>
/// <param name="InputTokens">What the call cost to send.</param>
/// <param name="OutputTokens">What the call cost to generate.</param>
public sealed record WireTurnCompletionEvent(
    int Attempt,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "completion";
}

/// <summary>Deterministic verification ran over the current answer.</summary>
/// <param name="Attempt">The attempt the checks ran over.</param>
/// <param name="Checks">The checks that ran, in the order a report lists them.</param>
/// <param name="Violations">How many violations they found.</param>
public sealed record WireTurnVerifyEvent(int Attempt, IReadOnlyList<string> Checks, int Violations) : IWireTurnEvent
{
    /// <inheritdoc />
    public string Stage => "verify";
}

/// <summary>The result of running a turn: what it produced, or why nothing was.</summary>
public readonly union WireRunTurnResult(WireTurnResponse, WireProblem);

/// <summary>The result of reading a session: the transcript, or why it could not be read.</summary>
public readonly union WireSessionResult(WireSession, WireProblem);

/// <summary>The result of closing a session: the session, or why it was not closed.</summary>
public readonly union WireCloseSessionResult(WireSessionClosed, WireProblem);

