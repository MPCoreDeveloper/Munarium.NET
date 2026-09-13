namespace Munarium.Wire;

using Munarium.Commands;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Shapes;

/// <summary>
/// The one implementation behind every transport.
/// </summary>
/// <remarks>
/// Both surfaces - the JSON/HTTP one, and the gRPC/protobuf one generated from the same
/// specification - are adapters over this class. A behaviour is therefore fixed once, and two
/// transports can only ever disagree about encoding, never about substance.
/// </remarks>
/// <param name="storage">The ledger's storage seam, for the head read.</param>
/// <param name="claims">The governed write path.</param>
/// <param name="facts">The pinned read model.</param>
/// <param name="shapes">The shapes claims are made under, and where lineage comes from.</param>
/// <param name="retrieval">The retrieval seam.</param>
/// <param name="embedder">The model provider that embeds a search question.</param>
/// <param name="embeddingModel">The embedding model to ask that provider for.</param>
public sealed class MunariumOperations(
    IStorageBackend storage,
    ClaimLedger claims,
    FactLedger facts,
    ShapeRegistry shapes,
    IRetrievalBackend retrieval,
    IModelProvider embedder,
    string embeddingModel)
{
    /// <summary>The wire contract version this implementation speaks.</summary>
    public const string Contract = "mmp.v1";

    /// <summary>What the contract says to return when the caller does not say how many chunks it wants.</summary>
    public const int DefaultTopK = 10;

    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly ClaimLedger _claims = claims ?? throw new ArgumentNullException(nameof(claims));
    private readonly FactLedger _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    private readonly ShapeRegistry _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
    private readonly IRetrievalBackend _retrieval = retrieval ?? throw new ArgumentNullException(nameof(retrieval));
    private readonly IModelProvider _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    private readonly string _embeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));

    /// <summary>Liveness.</summary>
    /// <returns>Healthy, and which contract is answering.</returns>
    public static WireHealth Health() => new("ok", Contract);

    /// <summary>The current head of a stream.</summary>
    /// <param name="stream">The stream to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The head.</returns>
    public async ValueTask<WireStreamHead> GetHeadAsync(
        string stream,
        CancellationToken cancellationToken = default)
    {
        var head = await _storage.HeadAsync(StreamId.From(stream), cancellationToken).ConfigureAwait(false);

        return new WireStreamHead(stream, head.Value);
    }

    /// <summary>
    /// Proposes a claim, which governance then judges.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="proposal">The claim as proposed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The claim as recorded, or a contended write if every retry lost.</returns>
    public async ValueTask<WireClaimResult> ProposeClaimAsync(
        string stream,
        WireClaimProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        // A proposal that does not carry what the contract requires is the caller's fault, and it is
        // answered as one: a 400 problem rather than a 500, and never a disputed claim, because
        // nothing about the ledger should be recorded for a request that was not understood.
        if (DescribeInvalid(proposal) is { } invalid)
        {
            return new WireProblem(
                "https://munarium.dev/problems/invalid-proposal",
                invalid,
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var outcome = await _claims.RecordAsync(
            new RecordClaimCommand
            {
                Stream = stream,
                ClaimId = proposal.ClaimId,
                Shape = proposal.Shape,
                Body = proposal.Body,
                Statement = proposal.Statement,
                Actor = proposal.Actor,
            },
            cancellationToken).ConfigureAwait(false);

        // Reported from the same derived identity the ledger wrote, so a caller can never be told a
        // lineage the kernel did not use.
        var lineage = _shapes.LineageOf(proposal.Shape, proposal.Body);

        return outcome switch
        {
            ClaimAsserted asserted => new WireClaimOutcome(
                stream, proposal.ClaimId, lineage, WireClaimStatus.Accepted, string.Empty, string.Empty, asserted.Head.Value),

            ClaimRecordedAsDisputed disputed => new WireClaimOutcome(
                stream, proposal.ClaimId, lineage, WireClaimStatus.Disputed, disputed.Gate, disputed.Reason, disputed.Head.Value),

            ClaimContended contended => new WireProblem(
                "https://munarium.dev/problems/contended-write",
                "Every retry lost to a moving head; the write was not recorded.",
                Status: 409,
                contended.Expected.Value,
                contended.Actual.Value),
        };
    }

    /// <summary>The facts that were current at a pin.</summary>
    /// <param name="asOf">The global position to read as of. 0 means the head.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The slice, and a digest over exactly the facts it contains.</returns>
    public async ValueTask<WireFactSlice> SliceFactsAsync(
        long asOf,
        CancellationToken cancellationToken = default)
    {
        // The contract says 0 means the head, so that is resolved here rather than left to the caller
        // to discover the watermark.
        var pin = asOf > 0
            ? new SequenceNumber(asOf)
            : await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false);

        var slice = await _facts.SliceAsync(pin, cancellationToken).ConfigureAwait(false);

        return new WireFactSlice(slice.Pin.Value, slice.Digest, [.. slice.Facts.Select(ToWire)]);
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

    private static WireFact ToWire(SlicedFact sliced) => new(
        sliced.Fact.ClaimId,
        sliced.Fact.Lineage,
        sliced.Fact.Statement,
        sliced.Fact.Actor,
        StatusOf(sliced.Fact),
        sliced.Fact.Gate,
        sliced.Fact.Reason,
        sliced.GlobalSequence.Value);

    private static WireSourceReference ToWire(SourceReference source) => new(
        source.ChunkId,
        source.SourceId,
        source.SourcePath,
        source.ContentHash,
        source.ChunkOrdinal);

    // Read from the recorded fact rather than re-judged, so a slice cannot disagree with the ledger.
    private static string StatusOf(FactRecord fact) =>
        fact.IsDisputed ? WireClaimStatus.Disputed : WireClaimStatus.Accepted;

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