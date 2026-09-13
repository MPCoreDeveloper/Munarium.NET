namespace Munarium.Server;

using Grpc.Core;
using Munarium.Wire;
using Munarium.Wire.Generated;

/// <summary>
/// The gRPC surface of the wire contract: the service base SharpPortico generated from the same
/// specification the JSON surface implements, over the same <see cref="MunariumOperations"/>.
/// </summary>
/// <remarks>
/// Every method is an adapter - it maps the contract's message onto the operation surface and back.
/// Nothing here decides anything about the ledger, so the two surfaces cannot disagree about substance,
/// only about encoding.
/// </remarks>
/// <param name="operations">The one implementation behind every transport.</param>
internal sealed class MunariumGrpcService(MunariumOperations operations) : MunariumServiceBase
{
    private readonly MunariumOperations _operations = operations ?? throw new ArgumentNullException(nameof(operations));

    /// <inheritdoc />
    public override Task<GetHealthResponse> GetHealthAsync(GetHealthRequest request, ServerCallContext context) =>
        Task.FromResult(new GetHealthResponse { Data = ToMessage(MunariumOperations.Health()) });

    /// <inheritdoc />
    public override async Task<GetHeadResponse> GetHeadAsync(GetHeadRequest request, ServerCallContext context)
    {
        var head = await _operations.GetHeadAsync(request.Stream, context.CancellationToken).ConfigureAwait(false);

        return new GetHeadResponse { Data = new StreamHead { Stream = head.Stream, Head = head.Head } };
    }

    /// <inheritdoc />
    public override async Task<ProposeClaimResponse> ProposeClaimAsync(
        ProposeClaimRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .ProposeClaimAsync(request.Stream, ToWire(request.Body), context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireClaimOutcome outcome => new ProposeClaimResponse { Data = ToMessage(outcome) },

            // The JSON surface answers 409 with the problem; gRPC's twin of that is ABORTED, which is
            // the retryable answer the specification describes.
            WireProblem problem => throw new RpcException(new Status(StatusCode.Aborted, problem.Detail)),
        };
    }

    /// <inheritdoc />
    public override async Task<SliceFactsResponse> SliceFactsAsync(
        SliceFactsRequest request,
        ServerCallContext context)
    {
        var slice = await _operations.SliceFactsAsync(request.AsOf, context.CancellationToken).ConfigureAwait(false);

        return new SliceFactsResponse { Data = ToMessage(slice) };
    }

    /// <inheritdoc />
    public override async Task<SearchResponse> SearchAsync(SearchRequest request, ServerCallContext context)
    {
        var query = new WireSearchQuery(request.Body?.Text ?? string.Empty, request.Body?.TopK ?? 0);
        var result = await _operations.SearchAsync(query, context.CancellationToken).ConfigureAwait(false);

        return new SearchResponse { Data = ToMessage(result) };
    }

    /// <inheritdoc />
    public override Task<ListShapesResponse> ListShapesAsync(ListShapesRequest request, ServerCallContext context) =>
        Task.FromResult(new ListShapesResponse { Data = ToMessage(_operations.ListShapes()) });

    // ---- the contract's model <-> the contract's messages ----
    // Mechanical by design: the two are the same contract in two encodings, so nothing here decides
    // anything - and a field added to the specification shows up as a compile error in both directions
    // rather than as a silent omission.

    private static Health ToMessage(WireHealth health) => new()
    {
        Status = health.Status,
        Contract = health.Contract,
    };

    private static ClaimOutcome ToMessage(WireClaimOutcome outcome) => new()
    {
        Stream = outcome.Stream,
        ClaimId = outcome.ClaimId,
        Lineage = outcome.Lineage,
        Status = StatusOf(outcome.Status),
        Gate = outcome.Gate,
        Reason = outcome.Reason,
        Head = outcome.Head,
    };

    private static FactSlice ToMessage(WireFactSlice slice)
    {
        var message = new FactSlice { AsOf = slice.AsOf, Digest = slice.Digest };

        foreach (var fact in slice.Facts)
        {
            message.Facts.Add(new Fact
            {
                ClaimId = fact.ClaimId,
                Lineage = fact.Lineage,
                Statement = fact.Statement,
                Actor = fact.Actor,
                Status = StatusOf(fact.Status),
                Gate = fact.Gate,
                Reason = fact.Reason,
                Sequence = fact.Sequence,
            });
        }

        return message;
    }

    private static SourceReference ToMessage(WireSourceReference source) => new()
    {
        ChunkId = source.ChunkId,
        SourceId = source.SourceId,
        SourcePath = source.SourcePath,
        ContentHash = source.ContentHash,
        ChunkOrdinal = source.ChunkOrdinal,
    };

    private static SearchResult ToMessage(WireSearchResult result)
    {
        var message = new SearchResult
        {
            Envelope = new ProvenanceEnvelope
            {
                IndexVersion = result.Envelope.IndexVersion,
                LedgerWatermark = result.Envelope.LedgerWatermark,
            },
        };

        foreach (var source in result.Envelope.Sources)
        {
            message.Envelope.Sources.Add(ToMessage(source));
        }

        foreach (var chunk in result.Chunks)
        {
            message.Chunks.Add(new RetrievedChunk
            {
                Source = ToMessage(chunk.Source),
                Score = chunk.Score,
                Text = chunk.Text,
            });
        }

        return message;
    }

    private static ShapeList ToMessage(WireShapeList shapes)
    {
        var message = new ShapeList();

        foreach (var shape in shapes.Shapes)
        {
            var item = new Shape { Name = shape.Name, Version = shape.Version, Schema = shape.Schema };
            item.Identity.AddRange(shape.Identity);
            message.Shapes.Add(item);
        }

        return message;
    }

    private static ClaimStatus StatusOf(string status) => status switch
    {
        WireClaimStatus.Accepted => ClaimStatus.Accepted,
        WireClaimStatus.Disputed => ClaimStatus.Disputed,
        WireClaimStatus.Contended => ClaimStatus.Contended,
        _ => ClaimStatus.Unspecified,
    };

    private static WireClaimProposal ToWire(ClaimProposal proposal) =>
        new(proposal.ClaimId, proposal.Shape, proposal.Body, proposal.Statement, proposal.Actor);
}
