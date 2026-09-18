namespace Munarium.Wire;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Munarium.Claims;
using Munarium.Commands;
using Munarium.Context;
using Munarium.Counters;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Promises;
using Munarium.Retrieval;
using Munarium.Shapes;
using Munarium.Sources;
using Munarium.Versions;

/// <summary>
/// The one implementation behind every transport.
/// </summary>
/// <remarks>
/// Both surfaces - the JSON/HTTP one, and the gRPC/protobuf one generated from the same specification -
/// are adapters over this class. A behaviour is therefore fixed once, and two transports can only ever
/// disagree about encoding, never about substance.
/// </remarks>
public sealed class MunariumOperations(
    IStorageBackend storage,
    ClaimLedger claims,
    CandidateLedger candidates,
    FindingsLedger findings,
    AnchorLedger anchors,
    PromiseLedger promises,
    CounterLedger counters,
    FactLedger facts,
    ShapeRegistry shapes,
    IRetrievalBackend retrieval,
    IModelProvider embedder,
    Composer composer,
    MeshSnapshotBuilder snapshots,
    string embeddingModel,
    IngestRunner ingest,
    ISourceRegistry sources,
    string tenant)
{
    /// <summary>The wire contract version this implementation speaks.</summary>
    public const string Contract = "mmp.v1";

    /// <summary>What the contract says to return when the caller does not say how many chunks it wants.</summary>
    public const int DefaultTopK = 10;

    /// <summary>The problem identifier a contended write answers with.</summary>
    public const string ContendedWriteProblem = "https://munarium.dev/problems/contended-write";

    /// <summary>The problem identifier a request that cannot be understood answers with.</summary>
    public const string InvalidRequestProblem = "https://munarium.dev/problems/invalid-request";

    /// <summary>The problem identifier a document this port cannot read answers with.</summary>
    public const string UnsupportedMediaTypeProblem = "https://munarium.dev/problems/unsupported-media-type";

    /// <summary>The problem identifier a document that is not the one declared answers with.</summary>
    public const string ContentHashMismatchProblem = "https://munarium.dev/problems/content-hash-mismatch";

    /// <summary>The problem identifier a source that was never ingested answers with.</summary>
    public const string UnknownSourceProblem = "https://munarium.dev/problems/unknown-source";


    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly ClaimLedger _claims = claims ?? throw new ArgumentNullException(nameof(claims));
    private readonly CandidateLedger _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
    private readonly FindingsLedger _findings = findings ?? throw new ArgumentNullException(nameof(findings));
    private readonly AnchorLedger _anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));
    private readonly PromiseLedger _promises = promises ?? throw new ArgumentNullException(nameof(promises));
    private readonly CounterLedger _counters = counters ?? throw new ArgumentNullException(nameof(counters));
    private readonly FactLedger _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    private readonly ShapeRegistry _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
    private readonly IRetrievalBackend _retrieval = retrieval ?? throw new ArgumentNullException(nameof(retrieval));
    private readonly IModelProvider _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    private readonly Composer _composer = composer ?? throw new ArgumentNullException(nameof(composer));
    private readonly MeshSnapshotBuilder _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly string _embeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));
    private readonly IngestRunner _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
    private readonly ISourceRegistry _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    private readonly string _tenant = string.IsNullOrWhiteSpace(tenant)
        ? throw new ArgumentException("The deployment's tenant must be named.", nameof(tenant))
        : tenant;


    /// <summary>Liveness.</summary>
    /// <returns>Healthy, and which contract is answering.</returns>
    public static WireHealth Health() => new("ok", Contract);

    /// <summary>The current head of a version's stream.</summary>
    /// <param name="versionId">The version to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The head.</returns>
    public async ValueTask<WireVersionHead> GetHeadAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var head = await _storage.HeadAsync(StreamId.From(versionId), cancellationToken).ConfigureAwait(false);

        return new WireVersionHead(versionId, head.Value);
    }

    /// <summary>Proposes a claim, which governance then judges.</summary>
    /// <param name="versionId">The version to write to.</param>
    /// <param name="proposal">The claim as proposed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The claim as recorded, or a problem when the write could not be made.</returns>
    public async ValueTask<WireClaimResult> ProposeClaimAsync(
        string versionId,
        WireClaimProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        // A proposal that does not carry what the contract requires is the caller's fault, and it is answered
        // as one: a 400 rather than a 500, and never a disputed claim, because nothing about the ledger
        // should be recorded for a request that was not understood.
        if (DescribeInvalid(proposal) is { } invalid)
        {
            return new WireProblem(InvalidRequestProblem, invalid, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var outcome = await _claims.RecordAsync(
            new RecordClaimCommand
            {
                VersionId = versionId,
                ClaimId = proposal.ClaimId,
                ClaimType = ClaimTypeOf(proposal.ClaimType),
                Shape = proposal.Shape,
                Body = proposal.Body,
                Statement = proposal.Statement,
                Actor = proposal.Actor,
            },
            cancellationToken).ConfigureAwait(false);

        // Reported from the same derived identity the ledger wrote, so a caller can never be told a lineage
        // the kernel did not use.
        var lineage = _shapes.LineageOf(proposal.Shape, proposal.Body);

        return outcome switch
        {
            ClaimAsserted asserted => new WireClaimOutcome(
                versionId, proposal.ClaimId, proposal.ClaimType, lineage,
                WireClaimStatus.Accepted, string.Empty, string.Empty, asserted.Head.Value),

            ClaimRecordedAsDisputed disputed => new WireClaimOutcome(
                versionId, proposal.ClaimId, proposal.ClaimType, lineage,
                WireClaimStatus.Disputed, disputed.Gate, disputed.Reason, disputed.Head.Value),

            ClaimContended contended => new WireProblem(
                ContendedWriteProblem,
                "Every retry lost to a moving head; the write was not recorded.",
                Status: 409,
                contended.Expected.Value,
                contended.Actual.Value),
        };
    }

    /// <summary>Proposes a batch of claims, which governance then judges as one unit.</summary>
    /// <param name="versionId">The version to write to.</param>
    /// <param name="request">The batch as proposed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The batch as recorded, or a problem when it was not recorded.</returns>
    /// <remarks>
    /// The whole unit is judged against the head the gates read and landed as one conditional append, so a
    /// batch cannot half-succeed. A blocked claim is recorded as disputed rather than dropped, which is why a
    /// 200 here can carry a disputed claim: the ledger did what it was asked and recorded the refusal with it.
    /// </remarks>
    public async ValueTask<WireClaimBatchResult> ProposeClaimBatchAsync(
        string versionId,
        WireClaimBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Claims.Count == 0)
        {
            return new WireProblem(
                InvalidRequestProblem,
                "claims must not be empty; a batch with nothing to judge is not a write.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var proposals = new List<ProposedClaim>(request.Claims.Count);

        for (var index = 0; index < request.Claims.Count; index++)
        {
            var claim = request.Claims[index];

            // The contract marks these required, so a caller that omits one is told which - and which claim.
            if (FirstMissing((claim.Subject, "subject"), (claim.Key, "key"), (claim.Value, "value")) is { } invalid)
            {
                return new WireProblem(
                    InvalidRequestProblem,
                    $"claims[{index}]: {invalid}",
                    Status: 400,
                    ExpectedHead: 0,
                    ActualHead: 0);
            }

            proposals.Add(new ProposedClaim
            {
                ClaimType = ClaimTypeOf(claim.ClaimType),
                Subject = claim.Subject,
                Key = claim.Key,
                Value = claim.Value,
                ScopePath = claim.ScopePath.Length > 0 ? claim.ScopePath : null,
                Provenance = ProvenanceOf(claim.Provenance),
                SupersedesId = claim.SupersedesId.Length > 0 ? claim.SupersedesId : null,
            });
        }

        // Zero means "no pin": a caller that names a position asks for that position and gets a contention
        // rather than a re-gate. Positions start at one, so zero is never a real head.
        var expectedHead = request.ExpectedHead > 0 ? new SequenceNumber(request.ExpectedHead) : (SequenceNumber?)null;

        var outcome = await _candidates
            .AppendAsync(versionId, proposals, request.Text, expectedHead, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            CandidateRecorded recorded => new WireClaimBatchOutcome(
                versionId,
                recorded.Head.Value,
                [
                    .. recorded.Claims.Select(claim =>
                        VerdictOf(versionId, claim, recorded.Findings, recorded.Head.Value)),
                ],
                [.. recorded.Findings.Select(FindingOf)],
                recorded.FindingsSequence?.Value ?? 0),

            CandidateContended contended => new WireProblem(
                ContendedWriteProblem,
                "Every retry lost to a moving head; the batch was not recorded.",
                Status: 409,
                contended.Expected.Value,
                contended.Actual.Value),
        };
    }

    /// <summary>Reads the findings a version's writes produced.</summary>
    /// <param name="versionId">The version whose stream is read.</param>
    /// <param name="asOf">The position to read as of, or 0 for every finding recorded.</param>
    /// <param name="severity">The severity to select, or empty for every severity.</param>
    /// <param name="ruleId">The exact rule to select, or empty.</param>
    /// <param name="rulePrefix">A rule-id prefix to select, or empty.</param>
    /// <param name="limit">How many findings to return, or 0 for all of them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The findings, oldest first.</returns>
    /// <remarks>
    /// The findings are read out of the version's own stream, which is where the write path recorded them: they
    /// live under the same pin as the claims they judged, so this read cannot see a verdict the ledger does not
    /// hold, and a finding recorded in the same append as its claims is citable by that position.
    /// </remarks>
    public async ValueTask<WireFindingList> ListFindingsAsync(
        string versionId,
        long asOf = 0,
        string? severity = null,
        string? ruleId = null,
        string? rulePrefix = null,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var query = new FindingsQuery
        {
            AsOfSequence = asOf > 0 ? new SequenceNumber(asOf) : null,
            Severity = SeverityOf(severity),
            RuleId = Optional(ruleId),
            RulePrefix = Optional(rulePrefix),
            Limit = limit > 0 ? limit : null,
        };

        var recorded = await _findings.ReadAsync(versionId, query, cancellationToken).ConfigureAwait(false);

        return new WireFindingList([.. recorded.Select(StoredOf)]);
    }

    /// <summary>Reads everything the mesh holds at one pin.</summary>
    /// <param name="versionId">The version to read, or empty for every version.</param>
    /// <param name="asOf">The position to read as of, or 0 for the present.</param>
    /// <param name="scope">A scope prefix to read, or empty for every scope.</param>
    /// <param name="factLimit">How many facts to keep, counting from the newest, or 0 for all of them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot at that pin.</returns>
    /// <remarks>
    /// The scope filter and the fact limit belong to the builder, applied after resolution, and the digest ladder
    /// is rebuilt there too: this operation turns the question into the builder's parameters and the answer into
    /// the contract's shape, and decides nothing itself.
    /// </remarks>
    public async ValueTask<WireSnapshot> LoadSnapshotAsync(
        string versionId,
        long asOf = 0,
        string? scope = null,
        int factLimit = 0,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _snapshots
            .BuildAsync(
                versionId,
                asOf > 0 ? new SequenceNumber(asOf) : null,
                Optional(scope),
                factLimit > 0 ? factLimit : null,
                cancellationToken)
            .ConfigureAwait(false);

        return new WireSnapshot(
            snapshot.VersionId,
            snapshot.AsOfSequence?.Value ?? 0,
            snapshot.AsOfDate ?? string.Empty,
            Timestamp(snapshot.WrittenAt),
            snapshot.WrittenOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            [.. snapshot.Facts.Select(ClaimOf)],
            [.. snapshot.Anchors.Values.Select(AnchorOf)],
            [.. snapshot.Digests.Select(DigestOf)],
            [.. snapshot.Promises.Select(PromiseOf)],
            [.. snapshot.Counters.Select(CounterOf)],
            [.. snapshot.Entities.Select(EntityOf)]);
    }

    /// <summary>Locks a detail, so no claim may contradict it.</summary>
    /// <param name="versionId">The version the lock is taken in.</param>
    /// <param name="request">The lock as asked for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lock as recorded, or why it was not taken.</returns>
    public async ValueTask<WireAnchorResult> LockAnchorAsync(
        string versionId,
        WireAnchorLock request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (FirstMissing((request.Subject, "subject"), (request.Key, "key"), (request.Value, "value")) is { } invalid)
        {
            return new WireProblem(InvalidRequestProblem, invalid, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        // The detail key is derived here rather than passed whole, so the dot that makes a lock matchable against a
        // claim is structural: the kernel refuses a key that names no property, and this cannot produce one.
        var outcome = await _anchors
            .LockAsync(
                versionId,
                string.Concat(request.Subject, ".", request.Key),
                request.Value,
                Optional(request.ScopePath),
                Optional(request.Evidence),
                cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            Anchor anchor => AnchorOf(anchor),
            WriteContended contended => Contended(contended),
        };
    }

    /// <summary>Releases a lock, if there is one.</summary>
    /// <param name="versionId">The version the release is recorded in.</param>
    /// <param name="detailKey">The locked detail, as <c>subject.key</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether anything was released, or why nothing was.</returns>
    public async ValueTask<WireReleaseResult> ReleaseAnchorAsync(
        string versionId,
        string detailKey,
        CancellationToken cancellationToken = default)
    {
        if (DetailKeyRefusal(detailKey) is { } refusal)
        {
            return refusal;
        }

        var outcome = await _anchors
            .ReleaseAsync(versionId, detailKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            Anchor => new WireAnchorRelease(Released: true),
            AnchorNotLocked => new WireAnchorRelease(Released: false),
            WriteContended contended => Contended(contended),
        };
    }

    /// <summary>Reads the locked details at a pin.</summary>
    /// <param name="versionId">The version whose locked details are read.</param>
    /// <param name="asOf">The position to read as of, or 0 for the present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The locks, later version winning and released ones absent.</returns>
    public async ValueTask<WireAnchorList> ListAnchorsAsync(
        string versionId,
        long asOf = 0,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(versionId, asOf, cancellationToken).ConfigureAwait(false);

        return new WireAnchorList([.. snapshot.Anchors.Values.Select(AnchorOf)]);
    }

    /// <summary>Registers a promise one scope owes to another.</summary>
    /// <param name="versionId">The version the promise is made in.</param>
    /// <param name="request">The promise as asked for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The promise as recorded, or why it was not registered.</returns>
    public async ValueTask<WirePromiseResult> OpenPromiseAsync(
        string versionId,
        WirePromiseRegistration request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (FirstMissing((request.Key, "key"), (request.Kind, "kind"), (request.Description, "description")) is { } invalid)
        {
            return new WireProblem(InvalidRequestProblem, invalid, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var outcome = await _promises
            .RegisterAsync(
                versionId,
                request.Key,
                request.Kind,
                request.Description,
                Optional(request.OriginScope),
                Optional(request.DueScope),
                cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            Promise promise => PromiseOf(promise),
            WriteContended contended => Contended(contended),
        };
    }

    /// <summary>Fulfils the first open promise with a key.</summary>
    /// <param name="versionId">The version the promise is fulfilled in.</param>
    /// <param name="key">The coordination key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether anything was fulfilled, or why nothing was.</returns>
    public async ValueTask<WireFulfilResult> FulfilPromiseAsync(
        string versionId,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new WireProblem(InvalidRequestProblem, "key is required.", Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var outcome = await _promises
            .FulfilAsync(versionId, key, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            Promise => new WirePromiseFulfilment(Fulfilled: true),
            PromiseNotOpen => new WirePromiseFulfilment(Fulfilled: false),
            WriteContended contended => Contended(contended),
        };
    }

    /// <summary>Reads the promises at a pin, and the overdue findings when they are asked for.</summary>
    /// <param name="versionId">The version whose promises are read.</param>
    /// <param name="asOf">The position to read as of, or 0 for the present.</param>
    /// <param name="status">The status to select, or empty for every status.</param>
    /// <param name="overdueScope">The scope to compute the promise check for, or empty to skip it.</param>
    /// <param name="isFinalUnit">Whether this read is the final unit, where every open promise is overdue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The promises, and the overdue findings when they were asked for.</returns>
    /// <remarks>
    /// The overdue view is computed over the full pinned slice, before the status filter narrows it: a finding a
    /// filter could hide would be a finding nobody sees, which is the opposite of what a check is for.
    /// </remarks>
    public async ValueTask<WirePromiseList> ListPromisesAsync(
        string versionId,
        long asOf = 0,
        string? status = null,
        string? overdueScope = null,
        bool isFinalUnit = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(versionId, asOf, cancellationToken).ConfigureAwait(false);
        var scope = Optional(overdueScope);

        var overdue = scope is not null || isFinalUnit
            ? PromiseRegistry.FindOverdue(snapshot.Promises, scope, isFinalUnit)
            : [];

        var selected = PromiseStatusOf(status) is { } wanted
            ? snapshot.Promises.Where(promise => promise.Status == wanted)
            : snapshot.Promises;

        return new WirePromiseList([.. selected.Select(PromiseOf)], [.. overdue.Select(FindingOf)]);
    }

    /// <summary>Records a whole-document total for a counter.</summary>
    /// <param name="versionId">The version the count belongs to.</param>
    /// <param name="request">The count as reported.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counter as recorded, or why it was not.</returns>
    /// <remarks>
    /// The contract counts in int64 and the plane in ulong, so a negative total is refused rather than wrapped: a
    /// count of minus three is not a smaller count, it is a request that cannot be understood.
    /// </remarks>
    public async ValueTask<WireCounterResult> RecordCounterAsync(
        string versionId,
        WireCounterRecording request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return new WireProblem(InvalidRequestProblem, "key is required.", Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        if (request.Total < 0 || request.Budget < 0)
        {
            return new WireProblem(
                InvalidRequestProblem,
                "total and budget are counts, so neither can be negative.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var outcome = await _counters
            .RecordAsync(
                versionId,
                request.Key,
                (ulong)request.Total,
                // Zero means "no ceiling", because a ceiling of zero would be a counter that may not be used at all -
                // which is a different statement, and one the contract has no way to make.
                request.Budget > 0 ? (ulong)request.Budget : null,
                cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            CounterTotal counter => CounterOf(counter),
            WriteContended contended => Contended(contended),
        };
    }

    /// <summary>Reads the counters at a pin, with the directives a writer would be given.</summary>
    /// <param name="versionId">The version whose counters are read.</param>
    /// <param name="asOf">The position to read as of, or 0 for the present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counters, and the directives that follow from them.</returns>
    /// <remarks>
    /// The directives are computed here rather than stored: they are a function of the totals, and a stored copy
    /// would be one more thing that can disagree with the plane it describes.
    /// </remarks>
    public async ValueTask<WireCounterList> ListCountersAsync(
        string versionId,
        long asOf = 0,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(versionId, asOf, cancellationToken).ConfigureAwait(false);

        return new WireCounterList(
            [.. snapshot.Counters.Select(CounterOf)],
            CounterBudget.Directives(snapshot.Counters));
    }

    /// <summary>Creates a version, which is itself a governed claim.</summary>
    /// <param name="request">The version to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or a problem when it could not be created.</returns>
    /// <remarks>
    /// A version is written as a claim under the <c>version</c> shape rather than into a table of its own, so
    /// it is judged by the same gates, pinned by the same feed and rebuilt from the same slice as everything
    /// else. It also makes a version id immutable by construction: claiming an existing id conflicts with the
    /// fact that already holds that lineage.
    /// </remarks>
    public async ValueTask<WireVersionResult> CreateVersionAsync(
        WireVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var versionId = request.VersionId.Trim();
        if (versionId.Length == 0)
        {
            // A generated version identity is a ULID, so the version carries the instant it was created at
            // without a field for it: see LedgerIds.
            versionId = LedgerIds.New();
        }

        var parent = request.ParentVersionId.Trim();
        var asOf = request.AsOfDate.Trim();
        var label = request.Label.Trim();

        if (asOf.Length > 0 &&
            !DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return new WireProblem(
                InvalidRequestProblem, "as_of_date must be a date as YYYY-MM-DD.", Status: 400, 0, 0);
        }

        if (parent.Length > 0)
        {
            var known = VersionRegistry.At(await PresentAsync(cancellationToken).ConfigureAwait(false));

            if (!known.TryResolve(parent, out _))
            {
                return new WireProblem(
                    InvalidRequestProblem,
                    $"no version '{parent}' exists, so '{versionId}' cannot descend from it.",
                    Status: 400,
                    0,
                    0);
            }
        }

        var statement = parent.Length > 0
            ? $"version '{versionId}' descends from '{parent}'"
            : $"version '{versionId}' starts a lineage";

        var outcome = await ProposeClaimAsync(
            versionId,
            new WireClaimProposal(
                versionId,
                WireClaimTypes.Fact,
                VersionRegistry.ShapeName,
                VersionBody(versionId, parent, asOf, label),
                statement,
                request.Actor ?? string.Empty),
            cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            WireClaimOutcome { Status: WireClaimStatus.Accepted } accepted =>
                new WireVersion(versionId, parent, asOf, label, accepted.Head),

            // A version whose own claim was refused never came into being; the refusal is in the ledger, and
            // the caller is told why rather than handed a version that governance did not accept.
            WireClaimOutcome refused => new WireProblem(
                InvalidRequestProblem,
                $"version '{versionId}' was refused by {refused.Gate}: {refused.Reason}",
                Status: 409,
                0,
                0),

            WireProblem problem => problem,
        };
    }

    /// <summary>The facts that were current at a pin.</summary>
    /// <param name="asOf">The global position to read as of. 0 means the present.</param>
    /// <param name="versionId">The version to read, or an empty string for every version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The slice, and a digest over exactly the facts it contains.</returns>
    public async ValueTask<WireFactSlice> SliceFactsAsync(
        long asOf,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        // The contract says 0 means the head, so that is resolved here rather than left to the caller to
        // discover the watermark.
        var pin = asOf > 0
            ? new SequenceNumber(asOf)
            : await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false);

        var slice = await _facts
            .SliceAsync(pin, versionId ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return new WireFactSlice(slice.Pin.Value, slice.Digest, [.. slice.Facts.Select(ToWire)]);
    }

    /// <summary>The path from a lineage root down to a version, inclusive.</summary>
    /// <param name="versionId">The version to read the lineage of.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The lineage, or an empty one when the version is not known at the present pin - which the transport
    /// answers as a 404, because a version that was never created is not a version with no ancestors.
    /// </returns>
    public async ValueTask<WireVersionLineage> GetLineageAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var registry = VersionRegistry.At(await PresentAsync(cancellationToken).ConfigureAwait(false));
        var versions = new List<WireVersion>();

        foreach (var version in registry.LineageOf(versionId))
        {
            versions.Add(await ToWireAsync(version, cancellationToken).ConfigureAwait(false));
        }

        return new WireVersionLineage(versions);
    }

    /// <summary>Composes the context a model would be given.</summary>
    /// <param name="request">What to compose.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The composed context, or a problem when the request could not be resolved.</returns>
    public async ValueTask<WireContextResult> ComposeContextAsync(
        WireContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (pin, problem) = await ResolvePinAsync(request, cancellationToken).ConfigureAwait(false);

        if (problem is not null)
        {
            return problem;
        }

        var composed = await _composer.ComposeAsync(
            new ContextRequest
            {
                Pin = pin,
                VersionId = request.VersionId ?? string.Empty,
                Shape = request.Shape ?? string.Empty,
                BudgetTokens = request.BudgetTokens,
                FactLimit = request.FactLimit,
            },
            cancellationToken).ConfigureAwait(false);

        return new WireComposedContext(
            [.. composed.Sections.Select(static section => new WireContextSection(section.Title, section.Body))],
            composed.Text,
            composed.EstimatedTokens,
            composed.ContentHash,
            composed.Pin.Value);
    }

    /// <summary>Retrieves evidence for a question.</summary>
    /// <param name="query">The question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chunks, and the envelope that seals them.</returns>
    public async ValueTask<WireSearchResult> SearchAsync(
        WireSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var embedded = await _embedder.EmbedAsync(
            new EmbeddingRequest { Model = _embeddingModel, Inputs = [query.Text] },
            cancellationToken).ConfigureAwait(false);

        var result = await _retrieval.SearchAsync(
            new RetrievalQuery
            {
                Text = query.Text,
                Embedding = embedded.Vectors[0],
                TopK = query.TopK > 0 ? query.TopK : DefaultTopK,
            },
            cancellationToken).ConfigureAwait(false);

        return new WireSearchResult(
            [.. result.Chunks.Select(chunk => new WireRetrievedChunk(ToWire(chunk.Source), chunk.Score, chunk.Text))],
            new WireProvenanceEnvelope(
                result.Envelope.IndexVersion,
                result.Envelope.LedgerWatermark.Value,
                [.. result.Envelope.Sources.Select(ToWire)]));
    }

    /// <summary>
    /// Ingests a document: the bytes are stored, the row is recorded, and the text is indexed.
    /// </summary>
    /// <remarks>
    /// The deployment's tenant is used rather than a caller-supplied one, because the contract carries no tenant yet:
    /// tenancy arrives with authorization, and the kernel's seams are already tenant-keyed, so nothing has to be
    /// reshaped when it does - only threaded. Until then a deployment is one tenant, and saying so is better than
    /// inventing a tenant per request.
    /// </remarks>
    /// <param name="request">The document as offered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document as stored and indexed, or why nothing was.</returns>
    public async ValueTask<WireIngestResult> IngestSourceAsync(
        WireSourceIngest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = (request.Path ?? string.Empty).Trim();

        if (path.Length == 0)
        {
            return new WireProblem(
                InvalidRequestProblem,
                "path is required: it is the source's identity and the address its bytes are stored at.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        DocumentOutcome outcome;

        try
        {
            outcome = await _ingest
                .IngestAsync(
                    _tenant,
                    path,
                    request.MediaType ?? string.Empty,
                    Encoding.UTF8.GetBytes(request.Content ?? string.Empty),
                    request.ContentSha256 is { Length: > 0 } declared ? declared : null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException invalid)
        {
            // A path a source may not hold, or a media type that names nothing, is a malformed request rather than a
            // document that was refused: nothing was attempted.
            return new WireProblem(InvalidRequestProblem, invalid.Message, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        return outcome switch
        {
            IngestedDocument ingested => ToWire(ingested),
            IngestRefused refused => new WireProblem(
                UnsupportedMediaTypeProblem,
                refused.Reason,
                Status: 415,
                ExpectedHead: 0,
                ActualHead: 0),
            IngestRejected rejected => new WireProblem(
                ContentHashMismatchProblem,
                $"the declared hash {rejected.Declared} is not the hash of the document that arrived "
                    + $"({rejected.Actual}), so nothing was written.",
                Status: 422,
                ExpectedHead: 0,
                ActualHead: 0),
        };
    }

    /// <summary>
    /// Reads where a document actually went.
    /// </summary>
    /// <param name="sourceId">The source's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or why there is none.</returns>
    public async ValueTask<WireSourceResult> GetSourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var record = await _sources.GetAsync(_tenant, sourceId, cancellationToken).ConfigureAwait(false);

        return record is null
            ? new WireProblem(
                UnknownSourceProblem,
                $"no source '{sourceId}' was ingested.",
                Status: 404,
                ExpectedHead: 0,
                ActualHead: 0)
            : ToWire(record);
    }

    /// <summary>The shapes this deployment understands.</summary>
    /// <returns>The registered shapes, ordered by name.</returns>
    public WireShapeList ListShapes() =>
        new([.. _shapes.Shapes.Select(
            shape => new WireShape(shape.Name, shape.Version, shape.Identity, shape.Schema))]);

    // ---- helpers ----

    private async ValueTask<FactSlice> PresentAsync(CancellationToken cancellationToken)
    {
        var pin = await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false);

        return await _facts.SliceAsync(pin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the pin a context is composed at: an explicit position, or a date read through the versions.
    /// </summary>
    private async ValueTask<(SequenceNumber Pin, WireProblem? Problem)> ResolvePinAsync(
        WireContextRequest request,
        CancellationToken cancellationToken)
    {
        var asOfDate = (request.AsOfDate ?? string.Empty).Trim();

        if (asOfDate.Length == 0)
        {
            return (new SequenceNumber(request.AsOf), null);
        }

        if (!DateOnly.TryParseExact(
                asOfDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (SequenceNumber.Zero, new WireProblem(
                InvalidRequestProblem, "as_of_date must be a date as YYYY-MM-DD.", Status: 400, 0, 0));
        }

        var registry = VersionRegistry.At(await PresentAsync(cancellationToken).ConfigureAwait(false));

        if (registry.EffectiveOn(date) is not { } effective ||
            !registry.TryPin(effective.VersionId, out var pin))
        {
            return (SequenceNumber.Zero, new WireProblem(
                "https://munarium.dev/problems/unknown-as-of-date",
                $"no version is as of {asOfDate} or earlier.",
                Status: 404,
                0,
                0));
        }

        return (pin, null);
    }

    private async ValueTask<WireVersion> ToWireAsync(VersionRecord version, CancellationToken cancellationToken)
    {
        var head = await _storage
            .HeadAsync(StreamId.From(version.VersionId), cancellationToken)
            .ConfigureAwait(false);

        return new WireVersion(
            version.VersionId,
            version.ParentVersionId,
            version.AsOfDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            version.Label,
            head.Value);
    }

    private static WireIngestedSource ToWire(IngestedDocument ingested) => new(
        ingested.Record.SourceId,
        ingested.Record.Path,
        KindOf(ingested.Kind),
        ingested.Record.MediaType,
        ingested.Record.ContentHash,
        ingested.Record.BytesLength,
        ingested.Record.BlobUri,
        ingested.Record.BackendId,
        ingested.Record.IngestedAt,
        ingested.ChunksIndexed,
        ingested.IndexVersion);

    private static WireSourceInfo ToWire(SourceRecord record) => new(
        record.SourceId,
        record.Path,
        record.MediaType,
        record.ContentHash,
        record.BytesLength,
        record.BlobUri,
        record.BackendId,
        record.IngestedAt);

    private static WireFact ToWire(SlicedFact sliced) => new(
        sliced.Fact.VersionId,
        sliced.Fact.ClaimId,
        ClaimTypeName(sliced.Fact.ClaimType),
        sliced.Fact.Lineage,
        sliced.Fact.Statement,
        sliced.Fact.Actor,
        sliced.Fact.IsDisputed ? WireClaimStatus.Disputed : WireClaimStatus.Accepted,
        sliced.Fact.Gate,
        sliced.Fact.Reason,
        sliced.GlobalSequence.Value);

    private static WireSourceReference ToWire(SourceReference source) => new(
        source.ChunkId,
        source.SourceId,
        source.SourcePath,
        source.ContentHash,
        source.ChunkOrdinal);

    private static string KindOf(SourceIngestKind kind) => kind switch
    {
        SourceIngestKind.Replaced => WireSourceKinds.Replaced,
        SourceIngestKind.Unchanged => WireSourceKinds.Unchanged,
        _ => WireSourceKinds.New,
    };

    private static string ClaimTypeName(ClaimType claimType) => claimType switch
    {
        ClaimType.Fact => WireClaimTypes.Fact,
        ClaimType.Update => WireClaimTypes.Update,
        ClaimType.Correction => WireClaimTypes.Correction,
        _ => WireClaimTypes.Unspecified,
    };

    // An unknown type is not a reason to refuse the claim: it is read as "the caller did not say", which is
    // what the ledger-conflict gate then treats as an assertion.
    private static ClaimType ClaimTypeOf(string? claimType) => claimType switch
    {
        WireClaimTypes.Fact => ClaimType.Fact,
        WireClaimTypes.Update => ClaimType.Update,
        WireClaimTypes.Correction => ClaimType.Correction,
        _ => ClaimType.Unspecified,
    };

    // The version body is built by hand rather than by a serialiser, because the body is ledger content: its
    // field order and escaping are part of what a digest is taken over. JsonEncodedText does the escaping so
    // nothing here has to be trusted to get a quote or a backslash right.
    private static string VersionBody(string versionId, string parent, string asOf, string label)
    {
        var body = new StringBuilder()
            .Append("{\"version_id\":\"").Append(JsonEncodedText.Encode(versionId)).Append('"');

        if (parent.Length > 0)
        {
            body.Append(",\"parent_version_id\":\"").Append(JsonEncodedText.Encode(parent)).Append('"');
        }

        if (asOf.Length > 0)
        {
            body.Append(",\"as_of\":\"").Append(JsonEncodedText.Encode(asOf)).Append('"');
        }

        if (label.Length > 0)
        {
            body.Append(",\"label\":\"").Append(JsonEncodedText.Encode(label)).Append('"');
        }

        return body.Append('}').ToString();
    }

    // The contract marks these required; a caller that omits one gets told which.
    private static string? DescribeInvalid(WireClaimProposal proposal) =>
        FirstMissing(
            (proposal.ClaimId, "claim_id"),
            (proposal.Shape, "shape"),
            (proposal.Body, "body"),
            (proposal.Statement, "statement"),
            (proposal.Actor, "actor"));

    private static string? FirstMissing(params (string? Value, string Name)[] fields)
    {
        foreach (var (value, name) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return $"{name} is required.";
            }
        }

        return null;
    }

    // The per-claim verdict, derived from the same blocked finding the write path matched. One rule, so the
    // wire and the ledger cannot disagree about why a claim is disputed.
    private static WireClaimOutcome VerdictOf(
        string versionId,
        Claim claim,
        IReadOnlyList<GateFinding> findings,
        long head)
    {
        var refusal = findings.FirstOrDefault(finding =>
            finding.Severity is Severity.Block &&
            string.Equals(finding.ClaimKey, claim.ClaimKey, StringComparison.Ordinal));

        return new WireClaimOutcome(
            versionId,
            claim.Id,
            ClaimTypeName(claim.ClaimType),
            claim.ClaimKey,
            claim.Status is ClaimStatus.Disputed ? WireClaimStatus.Disputed : WireClaimStatus.Accepted,
            refusal?.RuleId ?? string.Empty,
            refusal?.Message ?? string.Empty,
            head);
    }

    // The detail travels as the JSON text it already is: the kernel does not interpret it, so the contract
    // carries it verbatim rather than promising a shape it would then have to keep in step.
    private static WireFinding FindingOf(GateFinding finding) => new(
        finding.RuleId,
        SeverityName(finding.Severity),
        finding.Message,
        finding.ScopePath ?? string.Empty,
        finding.ClaimKey ?? string.Empty,
        finding.Detail?.ToJsonString() ?? string.Empty);

    private static WireStoredFinding StoredOf(StoredFinding stored) =>
        new(stored.Sequence.Value, FindingOf(stored.Finding));

    private async ValueTask<MeshSnapshot> SnapshotAsync(
        string versionId,
        long asOf,
        CancellationToken cancellationToken) =>
        await _snapshots
            .BuildAsync(versionId, asOf > 0 ? new SequenceNumber(asOf) : null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private static WireProblem Contended(WriteContended contended) => new(
        ContendedWriteProblem,
        "Every retry lost to a moving head; nothing was written.",
        Status: 409,
        contended.Expected.Value,
        contended.Actual.Value);

    // The release route carries the detail key whole, so the boundary checks the same rule the kernel enforces: a
    // key that names no property could never have been locked, and a request that cannot be understood is a 400
    // rather than a server error.
    private static WireProblem? DetailKeyRefusal(string? detailKey) =>
        string.IsNullOrWhiteSpace(detailKey) || !detailKey.Contains('.', StringComparison.Ordinal)
            ? new WireProblem(
                InvalidRequestProblem,
                "detail_key is 'subject.key', and this one names no property to release.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0)
            : null;

    // The plane holds what was recorded, so a read can select a status it actually contains; an unrecognised word
    // selects nothing rather than everything, because the caller asked for a state.
    private static PromiseStatus? PromiseStatusOf(string? status) => status switch
    {
        WirePromiseStatuses.Open => PromiseStatus.Open,
        WirePromiseStatuses.Fulfilled => PromiseStatus.Fulfilled,
        WirePromiseStatuses.Expired => PromiseStatus.Expired,
        WirePromiseStatuses.Violated => PromiseStatus.Violated,
        _ => null,
    };

    // The instant a snapshot carries, in the format the evidence contract validates a timestamp against: one
    // spelling of a timestamp in this port rather than one per surface.
    private static string Timestamp(DateTimeOffset? instant) =>
        instant?.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string ProvenanceName(Provenance provenance) => provenance switch
    {
        Provenance.Backfilled => WireProvenances.Backfilled,
        Provenance.Repaired => WireProvenances.Repaired,
        Provenance.Emergent => WireProvenances.Emergent,
        Provenance.CoverageRepair => WireProvenances.CoverageRepair,
        _ => WireProvenances.Witnessed,
    };

    private static WireResolvedClaim ClaimOf(Claim claim) => new(
        claim.Id,
        claim.VersionId,
        claim.Sequence.Value,
        ClaimTypeName(claim.ClaimType),
        claim.Subject,
        claim.Key,
        claim.Value,
        claim.ScopePath ?? string.Empty,
        claim.Status is ClaimStatus.Disputed ? WireClaimStatus.Disputed : WireClaimStatus.Accepted,
        ProvenanceName(claim.Provenance),
        claim.SupersedesId ?? string.Empty);

    private static WireAnchor AnchorOf(Anchor anchor) => new(
        anchor.Id,
        anchor.VersionId,
        anchor.DetailKey,
        anchor.LockedValue,
        anchor.LockedAtScope ?? string.Empty,
        AnchorStatusName(anchor.Status),
        anchor.Sequence.Value,
        anchor.EvidenceJson ?? string.Empty);

    private static WireDigest DigestOf(Digest digest) => new(
        digest.VersionId,
        digest.Tier,
        digest.ScopePath,
        digest.Content,
        digest.ContentHash,
        digest.BuiltFromSequence.Value);

    private static WirePromise PromiseOf(Promise promise) => new(
        promise.Id,
        promise.VersionId,
        promise.Key,
        promise.Kind,
        promise.Description,
        promise.OriginScope ?? string.Empty,
        promise.DueScope ?? string.Empty,
        PromiseStatusName(promise.Status),
        promise.Sequence.Value,
        promise.FulfilledSequence?.Value ?? 0);

    // The contract counts in int64: a count that does not fit is not a count, and a uint64 that did would be a
    // number no reader of the answer could act on.
    private static WireCounter CounterOf(CounterTotal counter) => new(
        counter.Key,
        (long)counter.Total,
        (long)(counter.Budget ?? 0),
        counter.IsOverBudget);

    private static WireEntity EntityOf(Entity entity) => new(
        entity.Id,
        entity.VersionId,
        entity.CanonicalName,
        entity.EntityType ?? string.Empty,
        entity.Aliases,
        entity.Sequence.Value,
        entity.MergedInto ?? string.Empty);

    private static string AnchorStatusName(AnchorStatus status) => status switch
    {
        AnchorStatus.Locked => WireAnchorStatuses.Locked,
        AnchorStatus.Released => WireAnchorStatuses.Released,
        _ => WireAnchorStatuses.Unspecified,
    };

    private static string PromiseStatusName(PromiseStatus status) => status switch
    {
        PromiseStatus.Open => WirePromiseStatuses.Open,
        PromiseStatus.Fulfilled => WirePromiseStatuses.Fulfilled,
        PromiseStatus.Expired => WirePromiseStatuses.Expired,
        PromiseStatus.Violated => WirePromiseStatuses.Violated,
        _ => WirePromiseStatuses.Unspecified,
    };

    // An empty query value means "do not filter", which is a different thing from filtering for the empty string:
    // no finding has an empty rule id, so a filter the caller did not ask for would only ever hide findings.
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // An unrecognised severity is not a filter: the caller asked for something the contract does not have, and
    // the honest answer to that is everything the version holds rather than an empty list that looks like a
    // verdict.
    private static Severity? SeverityOf(string? severity) => severity switch
    {
        WireSeverities.Info => Severity.Info,
        WireSeverities.Warn => Severity.Warn,
        WireSeverities.Block => Severity.Block,
        _ => null,
    };

    private static string SeverityName(Severity severity) => severity switch
    {
        Severity.Info => WireSeverities.Info,
        Severity.Warn => WireSeverities.Warn,
        Severity.Block => WireSeverities.Block,
        _ => WireSeverities.Unspecified,
    };

    // An unrecognised word reads as "the caller did not say" rather than as a refusal: a claim is not refused
    // for the provenance it carries, and guessing at a meaning would be worse than admitting there is none.
    private static Provenance ProvenanceOf(string? provenance) => provenance switch
    {
        WireProvenances.Backfilled => Provenance.Backfilled,
        WireProvenances.Repaired => Provenance.Repaired,
        WireProvenances.Emergent => Provenance.Emergent,
        WireProvenances.CoverageRepair => Provenance.CoverageRepair,
        _ => Provenance.Witnessed,
    };
}
