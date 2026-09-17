namespace Munarium.Wire;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Munarium.Commands;
using Munarium.Context;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Shapes;
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
    FactLedger facts,
    ShapeRegistry shapes,
    IRetrievalBackend retrieval,
    IModelProvider embedder,
    Composer composer,
    string embeddingModel)
{
    /// <summary>The wire contract version this implementation speaks.</summary>
    public const string Contract = "mmp.v1";

    /// <summary>What the contract says to return when the caller does not say how many chunks it wants.</summary>
    public const int DefaultTopK = 10;

    /// <summary>The problem identifier a contended write answers with.</summary>
    public const string ContendedWriteProblem = "https://munarium.dev/problems/contended-write";

    /// <summary>The problem identifier a request that cannot be understood answers with.</summary>
    public const string InvalidRequestProblem = "https://munarium.dev/problems/invalid-request";

    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly ClaimLedger _claims = claims ?? throw new ArgumentNullException(nameof(claims));
    private readonly FactLedger _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    private readonly ShapeRegistry _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
    private readonly IRetrievalBackend _retrieval = retrieval ?? throw new ArgumentNullException(nameof(retrieval));
    private readonly IModelProvider _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    private readonly Composer _composer = composer ?? throw new ArgumentNullException(nameof(composer));
    private readonly string _embeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));

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
}
