namespace Munarium.Server;

using Grpc.Core;
using Munarium.Wire;
using Munarium.Wire.Generated;

// The generated "Version" message and System.Version are both in scope, so the message gets a name of its
// own here rather than an ambiguity at every use.
using GeneratedVersion = Munarium.Wire.Generated.Version;

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
    public override async Task<CreateVersionResponse> CreateVersionAsync(
        CreateVersionRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .CreateVersionAsync(
                new WireVersionRequest(
                    request.Body?.VersionId ?? string.Empty,
                    request.Body?.ParentVersionId ?? string.Empty,
                    request.Body?.AsOfDate ?? string.Empty,
                    request.Body?.Label ?? string.Empty,
                    request.Body?.Actor ?? string.Empty),
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireVersion version => new CreateVersionResponse { Data = ToMessage(version) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<GetHeadResponse> GetHeadAsync(GetHeadRequest request, ServerCallContext context)
    {
        var head = await _operations
            .GetHeadAsync(request.VersionId, context.CancellationToken)
            .ConfigureAwait(false);

        return new GetHeadResponse
        {
            Data = new VersionHead { VersionId = head.VersionId, Head = head.Head },
        };
    }

    /// <inheritdoc />
    public override async Task<GetLineageResponse> GetLineageAsync(
        GetLineageRequest request,
        ServerCallContext context)
    {
        var lineage = await _operations
            .GetLineageAsync(request.VersionId, context.CancellationToken)
            .ConfigureAwait(false);

        // An empty lineage means no such version, which the JSON surface answers as a 404: a version that
        // exists always has itself in its own lineage.
        return lineage.Versions.Count == 0
            ? throw new RpcException(new Status(StatusCode.NotFound, $"no version '{request.VersionId}' exists."))
            : new GetLineageResponse { Data = ToMessage(lineage) };
    }

    /// <inheritdoc />
    public override async Task<ProposeClaimResponse> ProposeClaimAsync(
        ProposeClaimRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .ProposeClaimAsync(request.VersionId, ToWire(request.Body), context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireClaimOutcome outcome => new ProposeClaimResponse { Data = ToMessage(outcome) },

            // Contention is ABORTED here and 409 there, which is the retryable answer the specification
            // describes; a proposal that was not understood is INVALID_ARGUMENT and 400.
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ProposeClaimBatchResponse> ProposeClaimBatchAsync(
        ProposeClaimBatchRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .ProposeClaimBatchAsync(request.VersionId, ToWire(request.Body), context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireClaimBatchOutcome outcome => new ProposeClaimBatchResponse { Data = ToMessage(outcome) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<LoadSnapshotResponse> LoadSnapshotAsync(
        LoadSnapshotRequest request,
        ServerCallContext context)
    {
        var snapshot = await _operations
            .LoadSnapshotAsync(
                request.VersionId,
                request.AsOf,
                request.Scope,
                request.FactLimit,
                context.CancellationToken)
            .ConfigureAwait(false);

        return new LoadSnapshotResponse { Data = ToMessage(snapshot) };
    }

    /// <inheritdoc />
    public override async Task<ListFindingsResponse> ListFindingsAsync(
        ListFindingsRequest request,
        ServerCallContext context)
    {
        var findings = await _operations
            .ListFindingsAsync(
                request.VersionId,
                request.AsOf,
                request.Severity,
                request.RuleId,
                request.RulePrefix,
                request.Limit,
                context.CancellationToken)
            .ConfigureAwait(false);

        return new ListFindingsResponse { Data = ToMessage(findings) };
    }

    /// <inheritdoc />
    public override async Task<SliceFactsResponse> SliceFactsAsync(
        SliceFactsRequest request,
        ServerCallContext context)
    {
        var slice = await _operations
            .SliceFactsAsync(request.AsOf, request.VersionId ?? string.Empty, context.CancellationToken)
            .ConfigureAwait(false);

        return new SliceFactsResponse { Data = ToMessage(slice) };
    }

    /// <inheritdoc />
    public override async Task<ComposeContextResponse> ComposeContextAsync(
        ComposeContextRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .ComposeContextAsync(
                new WireContextRequest(
                    request.Body?.VersionId ?? string.Empty,
                    request.Body?.Shape ?? string.Empty,
                    request.Body?.AsOf ?? 0,
                    request.Body?.AsOfDate ?? string.Empty,
                    request.Body?.BudgetTokens ?? 0,
                    request.Body?.FactLimit ?? 0),
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireComposedContext composed => new ComposeContextResponse { Data = ToMessage(composed) },
            WireProblem problem => throw Problem(problem),
        };
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

    // A failure is one failure: the problem's own status decides the gRPC code, so the two surfaces answer
    // the same failure the same way instead of each inventing a mapping.
    private static RpcException Problem(WireProblem problem) =>
        new(new Status(CodeOf(problem.Status), problem.Detail));

    private static StatusCode CodeOf(int status) => status switch
    {
        400 => StatusCode.InvalidArgument,
        404 => StatusCode.NotFound,
        409 => StatusCode.Aborted,
        _ => StatusCode.Unknown,
    };

    private static Health ToMessage(WireHealth health) => new()
    {
        Status = health.Status,
        Contract = health.Contract,
    };

    private static GeneratedVersion ToMessage(WireVersion version) => new()
    {
        VersionId = version.VersionId,
        ParentVersionId = version.ParentVersionId,
        AsOfDate = version.AsOfDate,
        Label = version.Label,
        Head = version.Head,
    };

    private static VersionLineage ToMessage(WireVersionLineage lineage)
    {
        var message = new VersionLineage();

        foreach (var version in lineage.Versions)
        {
            message.Versions.Add(ToMessage(version));
        }

        return message;
    }

    private static ComposedContext ToMessage(WireComposedContext composed)
    {
        var message = new ComposedContext
        {
            Text = composed.Text,
            EstimatedTokens = composed.EstimatedTokens,
            ContentHash = composed.ContentHash,
            AsOf = composed.AsOf,
        };

        foreach (var section in composed.Sections)
        {
            message.Sections.Add(new ContextSection { Title = section.Title, Body = section.Body });
        }

        return message;
    }

    private static ClaimBatchOutcome ToMessage(WireClaimBatchOutcome outcome)
    {
        var message = new ClaimBatchOutcome
        {
            VersionId = outcome.VersionId,
            Head = outcome.Head,
            FindingsSequence = outcome.FindingsSequence,
        };

        foreach (var claim in outcome.Claims)
        {
            message.Claims.Add(ToMessage(claim));
        }

        foreach (var finding in outcome.Findings)
        {
            message.Findings.Add(ToMessage(finding));
        }

        return message;
    }

    private static Snapshot ToMessage(WireSnapshot snapshot)
    {
        var message = new Snapshot
        {
            VersionId = snapshot.VersionId,
            AsOfSequence = snapshot.AsOfSequence,
            AsOfDate = snapshot.AsOfDate,
            WrittenAt = snapshot.WrittenAt,
            WrittenOn = snapshot.WrittenOn,
        };

        foreach (var claim in snapshot.Facts)
        {
            message.Facts.Add(ToMessage(claim));
        }

        foreach (var anchor in snapshot.Anchors)
        {
            message.Anchors.Add(ToMessage(anchor));
        }

        foreach (var digest in snapshot.Digests)
        {
            message.Digests.Add(ToMessage(digest));
        }

        foreach (var promise in snapshot.Promises)
        {
            message.Promises.Add(ToMessage(promise));
        }

        foreach (var counter in snapshot.Counters)
        {
            message.Counters.Add(ToMessage(counter));
        }

        foreach (var entity in snapshot.Entities)
        {
            message.Entities.Add(ToMessage(entity));
        }

        return message;
    }

    private static ResolvedClaim ToMessage(WireResolvedClaim claim) => new()
    {
        ClaimId = claim.ClaimId,
        VersionId = claim.VersionId,
        Sequence = claim.Sequence,
        ClaimType = ClaimTypeOf(claim.ClaimType),
        Subject = claim.Subject,
        Key = claim.Key,
        Value = claim.Value,
        ScopePath = claim.ScopePath,
        Status = ClaimStatusOf(claim.Status),
        Provenance = ProvenanceOf(claim.Provenance),
        SupersedesId = claim.SupersedesId,
    };

    private static Anchor ToMessage(WireAnchor anchor) => new()
    {
        AnchorId = anchor.AnchorId,
        VersionId = anchor.VersionId,
        DetailKey = anchor.DetailKey,
        LockedValue = anchor.LockedValue,
        LockedAtScope = anchor.LockedAtScope,
        Status = AnchorStatusOf(anchor.Status),
        Sequence = anchor.Sequence,
        Evidence = anchor.Evidence,
    };

    private static Digest ToMessage(WireDigest digest) => new()
    {
        VersionId = digest.VersionId,
        Tier = digest.Tier,
        ScopePath = digest.ScopePath,
        Content = digest.Content,
        ContentHash = digest.ContentHash,
        BuiltFromSequence = digest.BuiltFromSequence,
    };

    private static Promise ToMessage(WirePromise promise) => new()
    {
        PromiseId = promise.PromiseId,
        VersionId = promise.VersionId,
        Key = promise.Key,
        Kind = promise.Kind,
        Description = promise.Description,
        OriginScope = promise.OriginScope,
        DueScope = promise.DueScope,
        Status = PromiseStatusOf(promise.Status),
        Sequence = promise.Sequence,
        FulfilledSequence = promise.FulfilledSequence,
    };

    private static Counter ToMessage(WireCounter counter) => new()
    {
        Key = counter.Key,
        Total = counter.Total,
        Budget = counter.Budget,
        OverBudget = counter.OverBudget,
    };

    private static Entity ToMessage(WireEntity entity)
    {
        var message = new Entity
        {
            EntityId = entity.EntityId,
            VersionId = entity.VersionId,
            CanonicalName = entity.CanonicalName,
            EntityType = entity.EntityType,
            Sequence = entity.Sequence,
            MergedInto = entity.MergedInto,
        };

        message.Aliases.AddRange(entity.Aliases);

        return message;
    }

    private static FindingList ToMessage(WireFindingList findings)
    {
        var message = new FindingList();

        foreach (var stored in findings.Findings)
        {
            message.Findings.Add(new StoredFinding
            {
                Sequence = stored.Sequence,
                Finding = ToMessage(stored.Finding),
            });
        }

        return message;
    }

    private static Finding ToMessage(WireFinding finding) => new()
    {
        RuleId = finding.RuleId,
        Severity = SeverityOf(finding.Severity),
        Message = finding.Message,
        ScopePath = finding.ScopePath,
        ClaimKey = finding.ClaimKey,
        Detail = finding.Detail,
    };

    private static ClaimOutcome ToMessage(WireClaimOutcome outcome) => new()
    {
        VersionId = outcome.VersionId,
        ClaimId = outcome.ClaimId,
        ClaimType = ClaimTypeOf(outcome.ClaimType),
        Lineage = outcome.Lineage,
        Status = ClaimStatusOf(outcome.Status),
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
                VersionId = fact.VersionId,
                ClaimId = fact.ClaimId,
                ClaimType = ClaimTypeOf(fact.ClaimType),
                Lineage = fact.Lineage,
                Statement = fact.Statement,
                Actor = fact.Actor,
                Status = ClaimStatusOf(fact.Status),
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

    // The contract's enumerations are names in the specification and numbers on the wire; these put the two
    // together, and each direction exists where the contract carries one: a status and a claim type travel
    // both ways, a provenance only into a write and a severity only out of a read. Every mapper is total, so
    // an unknown value becomes the contract's "unspecified" rather than a silent default.
    private static ClaimStatus ClaimStatusOf(string status) => status switch
    {
        WireClaimStatus.Accepted => ClaimStatus.Accepted,
        WireClaimStatus.Disputed => ClaimStatus.Disputed,
        WireClaimStatus.Contended => ClaimStatus.Contended,
        _ => ClaimStatus.Unspecified,
    };

    private static ClaimType ClaimTypeOf(string claimType) => claimType switch
    {
        WireClaimTypes.Fact => ClaimType.Fact,
        WireClaimTypes.Update => ClaimType.Update,
        WireClaimTypes.Correction => ClaimType.Correction,
        _ => ClaimType.Unspecified,
    };

    private static string ClaimTypeName(ClaimType claimType) => claimType switch
    {
        ClaimType.Fact => WireClaimTypes.Fact,
        ClaimType.Update => WireClaimTypes.Update,
        ClaimType.Correction => WireClaimTypes.Correction,
        _ => WireClaimTypes.Unspecified,
    };

    private static Provenance ProvenanceOf(string provenance) => provenance switch
    {
        WireProvenances.Backfilled => Provenance.Backfilled,
        WireProvenances.Repaired => Provenance.Repaired,
        WireProvenances.Emergent => Provenance.Emergent,
        WireProvenances.CoverageRepair => Provenance.CoverageRepair,
        _ => Provenance.Witnessed,
    };

    private static AnchorStatus AnchorStatusOf(string status) => status switch
    {
        WireAnchorStatuses.Locked => AnchorStatus.Locked,
        WireAnchorStatuses.Released => AnchorStatus.Released,
        _ => AnchorStatus.Unspecified,
    };

    private static PromiseStatus PromiseStatusOf(string status) => status switch
    {
        WirePromiseStatuses.Open => PromiseStatus.Open,
        WirePromiseStatuses.Fulfilled => PromiseStatus.Fulfilled,
        WirePromiseStatuses.Expired => PromiseStatus.Expired,
        WirePromiseStatuses.Violated => PromiseStatus.Violated,
        _ => PromiseStatus.Unspecified,
    };

    private static string ProvenanceName(Provenance provenance) => provenance switch
    {
        Provenance.Witnessed => WireProvenances.Witnessed,
        Provenance.Backfilled => WireProvenances.Backfilled,
        Provenance.Repaired => WireProvenances.Repaired,
        Provenance.Emergent => WireProvenances.Emergent,
        Provenance.CoverageRepair => WireProvenances.CoverageRepair,
        _ => WireProvenances.Unspecified,
    };

    private static Severity SeverityOf(string severity) => severity switch
    {
        WireSeverities.Info => Severity.Info,
        WireSeverities.Warn => Severity.Warn,
        WireSeverities.Block => Severity.Block,
        _ => Severity.Unspecified,
    };

    private static WireClaimBatchRequest ToWire(ClaimBatchRequest? body)
    {
        var claims = new List<WireClaimCandidate>();

        if (body is not null)
        {
            foreach (var candidate in body.Claims)
            {
                claims.Add(ToWire(candidate));
            }
        }

        return new WireClaimBatchRequest(claims, body?.Text ?? string.Empty, body?.ExpectedHead ?? 0);
    }

    private static WireClaimCandidate ToWire(ClaimCandidate candidate) => new(
        ClaimTypeName(candidate.ClaimType),
        candidate.Subject,
        candidate.Key,
        candidate.Value,
        candidate.ScopePath,
        ProvenanceName(candidate.Provenance),
        candidate.SupersedesId);

    private static WireClaimProposal ToWire(ClaimProposal? proposal) => new(
        proposal?.ClaimId ?? string.Empty,
        ClaimTypeName(proposal?.ClaimType ?? ClaimType.Unspecified),
        proposal?.Shape ?? string.Empty,
        proposal?.Body ?? string.Empty,
        proposal?.Statement ?? string.Empty,
        proposal?.Actor ?? string.Empty);
}
