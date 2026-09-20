namespace Munarium.Server;

using Munarium.Access;

using Grpc.Core;
using Munarium.Wire;
using Munarium.Wire.Generated;

// The generated "Version" message and System.Version are both in scope, so the message gets a name of its
// own here rather than an ambiguity at every use.
using GeneratedVersion = Munarium.Wire.Generated.Version;

// The kernel's evidence types and the generated evidence messages share several names - a manifest, a schema, a
// column - so the kernel's namespace is aliased and the generated names stay unqualified everywhere below. The
// generated side is where the messages are referenced, which is where the shorter name earns its keep.
using Evidence = Munarium.Evidence;


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
/// <param name="kernel">The deployment whose gate every plane resolves through.</param>
internal sealed class MunariumGrpcService(MunariumOperations operations, MunariumKernel kernel) : MunariumServiceBase
{
    private readonly MunariumOperations _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    private readonly MunariumKernel _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));

    /// <summary>Resolves the caller's principal through the gate, exactly as the JSON surface does.</summary>
    /// <remarks>
    /// Both transports resolve here rather than each deciding for itself: a gRPC call carries the same bearer metadata,
    /// and a plane that served the deployment principal while the JSON one demanded a capability would be one deployment
    /// answering two ways. That asymmetry was here - the evidence and session calls resolved the fallback directly - and
    /// the reason is recorded rather than explained away.
    /// </remarks>
    /// <param name="context">The call, whose metadata carries the capability.</param>
    /// <param name="scope">The scope the plane requires.</param>
    /// <returns>The principal.</returns>
    private async ValueTask<Munarium.Evidence.EvidencePrincipal> PrincipalAsync(ServerCallContext context, string scope)
    {
        var access = await _kernel
            .Gate.ResolveAsync(Authorization(context), scope, DateTimeOffset.UtcNow, context.CancellationToken)
            .ConfigureAwait(false);

        if (access is Munarium.Evidence.EvidencePrincipal principal)
        {
            return principal;
        }

        var reason = access is AccessRefused refused ? refused.Reason : AccessGate.MissingReason;

        throw Problem(new WireProblem(
            MunariumOperations.UnauthorizedProblem,
            reason,
            Status: 401,
            ExpectedHead: 0,
            ActualHead: 0));
    }

    /// <summary>Reads the bearer capability out of a call's metadata.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The header's value, or <see langword="null"/> when none was sent.</returns>
    private static string? Authorization(ServerCallContext context) =>
        context.RequestHeaders.FirstOrDefault(entry =>
            string.Equals(entry.Key, "authorization", StringComparison.OrdinalIgnoreCase))?.Value;

    /// <inheritdoc />
    public override async Task<CreateAuthoringDraftResponse> CreateAuthoringDraftAsync(
        CreateAuthoringDraftRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _operations
            .OpenDraftAsync(
                new WireAuthoringDraftRequest(request.Body?.Name ?? string.Empty, request.Body?.PatternId),
                context.CancellationToken)
            .ConfigureAwait(false) switch
        {
            WireAuthoringDraft draft => new CreateAuthoringDraftResponse { Data = Draft(draft) },
            WireProblem problem => throw Problem(problem),
            var other => throw new InvalidOperationException($"unexpected draft result: {other}"),
        };
    }

    /// <inheritdoc />
    public override async Task<ListAuthoringDraftsResponse> ListAuthoringDraftsAsync(
        ListAuthoringDraftsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var listed = await _operations.ListDraftsAsync(context.CancellationToken).ConfigureAwait(false);

        return new ListAuthoringDraftsResponse
        {
            Data = new AuthoringDraftList { Drafts = { listed.Drafts.Select(Draft) } },
        };
    }

    /// <inheritdoc />
    public override async Task<GetAuthoringDraftResponse> GetAuthoringDraftAsync(
        GetAuthoringDraftRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _operations
            .ReadDraftAsync(request.DraftId, context.CancellationToken)
            .ConfigureAwait(false) switch
        {
            WireAuthoringDraft draft => new GetAuthoringDraftResponse { Data = Draft(draft) },
            WireProblem problem => throw Problem(problem),
            var other => throw new InvalidOperationException($"unexpected draft result: {other}"),
        };
    }

    /// <inheritdoc />
    public override async Task<PutAuthoringDraftAnswersResponse> PutAuthoringDraftAnswersAsync(
        PutAuthoringDraftAnswersRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _operations
            .AnswerDraftAsync(request.DraftId, Answers(request.Body), context.CancellationToken)
            .ConfigureAwait(false) switch
        {
            WireAuthoringDraft draft => new PutAuthoringDraftAnswersResponse { Data = Draft(draft) },
            WireProblem problem => throw Problem(problem),
            var other => throw new InvalidOperationException($"unexpected draft result: {other}"),
        };
    }

    /// <inheritdoc />
    public override async Task<ValidateAuthoringDraftResponse> ValidateAuthoringDraftAsync(
        ValidateAuthoringDraftRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _operations
            .ValidateDraftAsync(request.DraftId, context.CancellationToken)
            .ConfigureAwait(false) switch
        {
            WireDraftValidation validation => new ValidateAuthoringDraftResponse { Data = Validation(validation) },
            WireProblem problem => throw Problem(problem),
            var other => throw new InvalidOperationException($"unexpected validation result: {other}"),
        };
    }

    /// <inheritdoc />
    public override async Task<DeleteAuthoringDraftResponse> DeleteAuthoringDraftAsync(
        DeleteAuthoringDraftRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _operations
            .DeleteDraftAsync(request.DraftId, context.CancellationToken)
            .ConfigureAwait(false) switch
        {
            WireAuthoringDraftRemoved removed => new DeleteAuthoringDraftResponse
            {
                Data = new AuthoringDraftRemoved { Name = removed.Name },
            },
            WireProblem problem => throw Problem(problem),
            var other => throw new InvalidOperationException($"unexpected removal result: {other}"),
        };
    }

    /// <summary>Carries a draft as the contract carries it.</summary>
    /// <param name="draft">The draft.</param>
    /// <returns>The message.</returns>
    private static AuthoringDraft Draft(WireAuthoringDraft draft) => new()
    {
        Name = draft.Name,
        PatternId = draft.PatternId ?? string.Empty,
        CreatedAt = draft.CreatedAt ?? string.Empty,
        UpdatedAt = draft.UpdatedAt ?? string.Empty,
        Answers = { draft.Answers.Select(entry => new AuthoringAnswer { Name = entry.Key, Value = Value(entry.Value) }) },
        Sections = { draft.Sections.Select(Section) },
        Todos = { draft.Todos },
    };

    /// <summary>Carries one interview section as the contract carries it.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The message.</returns>
    private static AuthoringDraftSection Section(WireAuthoringDraftSection section) => new()
    {
        Id = section.Id,
        Title = section.Title,
        DocRef = section.DocRef,
        Questions = { section.Questions.Select(Question) },
    };

    /// <summary>Carries one question as the contract carries it.</summary>
    /// <param name="question">The question.</param>
    /// <returns>The message.</returns>
    private static AuthoringQuestion Question(WireAuthoringQuestion question) => new()
    {
        Id = question.Id,
        Prompt = question.Prompt,
        Guidance = question.Guidance,
        Kind = question.Kind,
        Required = question.Required,
        Default = question.Default ?? string.Empty,
        Choices = { question.Choices },
        MapsTo = question.MapsTo,
    };

    /// <summary>Carries a validation as the contract carries it.</summary>
    /// <param name="validation">The validation.</param>
    /// <returns>The message.</returns>
    private static AuthoringDraftValidation Validation(WireDraftValidation validation) => new()
    {
        Valid = validation.Valid,
        Findings = { validation.Findings.Select(Finding) },
        Todos = { validation.Todos },
    };

    /// <summary>Carries one finding as the contract carries it.</summary>
    /// <param name="finding">The finding.</param>
    /// <returns>The message.</returns>
    private static ValidationFinding Finding(WireValidationFinding finding) => new()
    {
        Severity = finding.Severity,
        Code = finding.Code,
        Message = finding.Message,
        Path = finding.Path,
    };

    /// <summary>Carries one answer as the contract carries it.</summary>
    /// <param name="value">The answer.</param>
    /// <returns>The message.</returns>
    private static AuthoringValue Value(WireAuthoringValue value) => new()
    {
        Text = value.Text ?? string.Empty,
        Number = value.Number ?? 0,
        Flag = value.Flag ?? false,
        Items = { value.Items?.Select(Value) ?? [] },
        Fields = {
            value.Fields?.Select(entry => new AuthoringAnswer { Name = entry.Key, Value = Value(entry.Value) })
                ?? []
        },
    };

    /// <summary>Reads the answers a caller sent.</summary>
    /// <param name="answers">The message.</param>
    /// <returns>The answers.</returns>
    private static WireAuthoringAnswers Answers(AuthoringAnswers? answers) =>
        new(new Dictionary<string, WireAuthoringValue>(
            (answers?.Answers ?? []).Select(answer => new KeyValuePair<string, WireAuthoringValue>(
                answer.Name, WireValue(answer.Value))),
            StringComparer.Ordinal));

    /// <summary>Reads one answer a caller sent.</summary>
    /// <param name="value">The message.</param>
    /// <returns>The answer.</returns>
    private static WireAuthoringValue WireValue(AuthoringValue? value)
    {
        if (value is null)
        {
            return new WireAuthoringValue(null, null, null, null, null);
        }

        List<WireAuthoringValue>? items = value.Items.Count == 0 ? null : [.. value.Items.Select(WireValue)];
        Dictionary<string, WireAuthoringValue>? fields = value.Fields.Count == 0
            ? null
            : new Dictionary<string, WireAuthoringValue>(
                value.Fields.Select(field => new KeyValuePair<string, WireAuthoringValue>(
                    field.Name, WireValue(field.Value))),
                StringComparer.Ordinal);

        return new WireAuthoringValue(
            string.IsNullOrEmpty(value.Text) ? null : value.Text,
            value.Number,
            value.Flag,
            items,
            fields);
    }

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
                    request.Body?.Actor ?? string.Empty,
                    request.Body?.IdempotencyKey ?? string.Empty),
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
    public override async Task<LockAnchorResponse> LockAnchorAsync(
        LockAnchorRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .LockAnchorAsync(
                request.VersionId,
                new WireAnchorLock(
                    request.Body?.Subject ?? string.Empty,
                    request.Body?.Key ?? string.Empty,
                    request.Body?.Value ?? string.Empty,
                    request.Body?.ScopePath ?? string.Empty,
                    request.Body?.Evidence ?? string.Empty,
                    request.Body?.IdempotencyKey ?? string.Empty),
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireAnchor anchor => new LockAnchorResponse { Data = ToMessage(anchor) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ListAnchorsResponse> ListAnchorsAsync(
        ListAnchorsRequest request,
        ServerCallContext context)
    {
        var anchors = await _operations
            .ListAnchorsAsync(request.VersionId, request.AsOf, context.CancellationToken)
            .ConfigureAwait(false);

        var message = new AnchorList();

        foreach (var anchor in anchors.Anchors)
        {
            message.Anchors.Add(ToMessage(anchor));
        }

        return new ListAnchorsResponse { Data = message };
    }

    /// <inheritdoc />
    public override async Task<ReleaseAnchorResponse> ReleaseAnchorAsync(
        ReleaseAnchorRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .ReleaseAnchorAsync(request.VersionId, request.DetailKey, request.IdempotencyKey, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireAnchorRelease released => new ReleaseAnchorResponse
            {
                Data = new AnchorRelease { Released = released.Released },
            },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<OpenPromiseResponse> OpenPromiseAsync(
        OpenPromiseRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .OpenPromiseAsync(
                request.VersionId,
                new WirePromiseRegistration(
                    request.Body?.Key ?? string.Empty,
                    request.Body?.Kind ?? string.Empty,
                    request.Body?.Description ?? string.Empty,
                    request.Body?.OriginScope ?? string.Empty,
                    request.Body?.DueScope ?? string.Empty,
                    request.Body?.IdempotencyKey ?? string.Empty),
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WirePromise promise => new OpenPromiseResponse { Data = ToMessage(promise) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ListPromisesResponse> ListPromisesAsync(
        ListPromisesRequest request,
        ServerCallContext context)
    {
        var promises = await _operations
            .ListPromisesAsync(
                request.VersionId,
                request.AsOf,
                request.Status,
                request.OverdueScope,
                request.Final,
                context.CancellationToken)
            .ConfigureAwait(false);

        var message = new PromiseList();

        foreach (var promise in promises.Promises)
        {
            message.Promises.Add(ToMessage(promise));
        }

        foreach (var finding in promises.Findings)
        {
            message.Findings.Add(ToMessage(finding));
        }

        return new ListPromisesResponse { Data = message };
    }

    /// <inheritdoc />
    public override async Task<FulfillPromiseResponse> FulfillPromiseAsync(
        FulfillPromiseRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .FulfilPromiseAsync(request.VersionId, request.Key, request.IdempotencyKey, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WirePromiseFulfilment fulfilled => new FulfillPromiseResponse
            {
                Data = new PromiseFulfilment { Fulfilled = fulfilled.Fulfilled },
            },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<RecordCounterResponse> RecordCounterAsync(
        RecordCounterRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .RecordCounterAsync(
                request.VersionId,
                new WireCounterRecording(
                    request.Body?.Key ?? string.Empty,
                    request.Body?.Total ?? 0,
                    request.Body?.Budget ?? 0,
                    request.Body?.IdempotencyKey ?? string.Empty),
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireCounter counter => new RecordCounterResponse { Data = ToMessage(counter) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ListCountersResponse> ListCountersAsync(
        ListCountersRequest request,
        ServerCallContext context)
    {
        var counters = await _operations
            .ListCountersAsync(request.VersionId, request.AsOf, context.CancellationToken)
            .ConfigureAwait(false);

        var message = new CounterList { Directives = counters.Directives };

        foreach (var counter in counters.Counters)
        {
            message.Counters.Add(ToMessage(counter));
        }

        return new ListCountersResponse { Data = message };
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
    public override async Task<IngestSourceResponse> IngestSourceAsync(
        IngestSourceRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .IngestSourceAsync(
                new WireSourceIngest(
                    request.Body?.Path ?? string.Empty,
                    request.Body?.MediaType ?? string.Empty,
                    request.Body?.Content ?? string.Empty,
                    request.Body?.ContentSha256 ?? string.Empty,
                request.Body?.ContentBase64),
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireIngestedSource ingested => new IngestSourceResponse { Data = ToMessage(ingested) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<GetSourceResponse> GetSourceAsync(
        GetSourceRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .GetSourceAsync(request.SourceId, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireSourceInfo info => new GetSourceResponse { Data = ToMessage(info) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public override async Task<IssueAccessTokenResponse> IssueAccessTokenAsync(
        IssueAccessTokenRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _operations.IssueAccessTokenAsync(
            new WireAccessTokenRequest(
                request.Body?.Subject ?? string.Empty,
                request.Body?.Level ?? 0,
                [.. request.Body?.Compartments ?? []],
                [.. request.Body?.Scopes ?? []],
                request.Body?.Runbooks is { Count: > 0 } runbooks ? [.. runbooks] : null,
                request.Body?.LifetimeSeconds ?? 0),
            MunariumKernel.AccessSecret,
            DateTimeOffset.UtcNow,
            context.CancellationToken);

        return result switch
        {
            WireAccessToken issued => new IssueAccessTokenResponse
            {
                Data = new AccessToken
                {
                    Token = issued.Token,
                    TokenId = issued.TokenId,
                    ExpiresAt = issued.ExpiresAt,
                },
            },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ListAccessTokensResponse> ListAccessTokensAsync(
        ListAccessTokensRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var audit = await _operations
            .ListAccessTokensAsync(context.CancellationToken)
            .ConfigureAwait(false);

        return AccessGrpcMapping.ToMessage(audit);
    }

    /// <inheritdoc />
    public override async Task<RevokeAccessTokenResponse> RevokeAccessTokenAsync(
        RevokeAccessTokenRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _operations
            .RevokeAccessTokenAsync(
                request.Jti ?? string.Empty,
                DateTimeOffset.UtcNow,
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireAccessTokenAudit withdrawn => new RevokeAccessTokenResponse
            {
                Data = AccessGrpcMapping.ToMessage(withdrawn),
            },
            WireProblem problem => throw Problem(problem),
        };
    }

    public override async Task<BuildIndexVersionResponse> BuildIndexVersionAsync(
        BuildIndexVersionRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .BuildIndexVersionAsync(
                new WireIndexBuild(
                    request.Body?.CollectionId ?? string.Empty,
                    request.Body?.CollectionName ?? string.Empty,
                    request.Body?.ShapeRef ?? string.Empty,
                    request.Body?.PathPrefix ?? string.Empty,
                    request.Body?.Activate ?? false),
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireIndexVersion version => new BuildIndexVersionResponse { Data = ToMessage(version) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<GetIndexVersionResponse> GetIndexVersionAsync(
        GetIndexVersionRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .GetIndexVersionAsync(request.IndexVersionId, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireIndexVersion version => new GetIndexVersionResponse { Data = ToMessage(version) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<GetActiveIndexVersionResponse> GetActiveIndexVersionAsync(
        GetActiveIndexVersionRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .GetActiveIndexVersionAsync(request.CollectionId, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireIndexVersion version => new GetActiveIndexVersionResponse { Data = ToMessage(version) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ActivateIndexVersionResponse> ActivateIndexVersionAsync(
        ActivateIndexVersionRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .ActivateIndexVersionAsync(
                request.IndexVersionId,
                request.Body?.CollectionId ?? string.Empty,
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireIndexVersion version => new ActivateIndexVersionResponse { Data = ToMessage(version) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ResolveEnvelopeResponse> ResolveEnvelopeAsync(
        ResolveEnvelopeRequest request,
        ServerCallContext context)
    {
        var body = request.Body;

        var result = await _operations
            .ResolveEnvelopeAsync(
                new WireEnvelopeQuery(
                    body?.IndexVersion ?? string.Empty,
                    body?.LedgerWatermark ?? 0,
                    [.. (body?.Sources ?? []).Select(ToWire)]),
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireEnvelopeResolution resolution => new ResolveEnvelopeResponse
            {
                Data = ToMessage(resolution),
            },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ListSourcesResponse> ListSourcesAsync(
        ListSourcesRequest request,
        ServerCallContext context)
    {
        var sources = await _operations
            .ListSourcesAsync(request.PathPrefix, context.CancellationToken)
            .ConfigureAwait(false);

        var message = new SourceList();

        foreach (var source in sources.Sources)
        {
            message.Sources.Add(ToMessage(source));
        }

        return new ListSourcesResponse { Data = message };
    }

    /// <inheritdoc />
    public override async Task<ListIndexVersionsResponse> ListIndexVersionsAsync(
        ListIndexVersionsRequest request,
        ServerCallContext context)
    {
        var versions = await _operations
            .ListIndexVersionsAsync(request.CollectionId, context.CancellationToken)
            .ConfigureAwait(false);

        var message = new IndexVersionList { CollectionId = versions.CollectionId };

        foreach (var version in versions.Versions)
        {
            message.Versions.Add(ToMessage(version));
        }

        return new ListIndexVersionsResponse { Data = message };
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
    /// <inheritdoc />
    public override Task<ListAuthoringPatternsResponse> ListAuthoringPatternsAsync(
        ListAuthoringPatternsRequest request,
        ServerCallContext context) =>
        Task.FromResult(new ListAuthoringPatternsResponse
        {
            Data = ToMessage(MunariumOperations.ListAuthoringPatterns()),
        });

    /// <inheritdoc />
    public override Task<GetAuthoringPatternResponse> GetAuthoringPatternAsync(
        GetAuthoringPatternRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = MunariumOperations.AuthoringPattern(request.Id ?? string.Empty);

        return Task.FromResult(result switch
        {
            WireAuthoringPattern pattern => new GetAuthoringPatternResponse { Data = ToMessage(pattern) },
            WireProblem problem => throw Problem(problem),
        });
    }

    private static RpcException Problem(WireProblem problem) =>
        new(new Status(CodeOf(problem.Status), problem.Detail));

    private static StatusCode CodeOf(int status) => status switch
    {
        401 => StatusCode.Unauthenticated,
        400 => StatusCode.InvalidArgument,
        404 => StatusCode.NotFound,
        409 => StatusCode.Aborted,

        // The JSON surface can say which of "the document is not the one declared" and "no extractor reads that media
        // type" it means, because HTTP has a status for each. gRPC does not have that fine a scale, so both are
        // InvalidArgument here and the detail carries which: a coarser code is honest, an invented one would not be.
        415 => StatusCode.InvalidArgument,
        422 => StatusCode.InvalidArgument,
        _ => StatusCode.Unknown,
    };

    /// <inheritdoc />
    public override async Task<CreateSessionResponse> CreateSessionAsync(
        CreateSessionRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .CreateSessionAsync(request.Name, await PrincipalAsync(context, AccessScope.Query), context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireSessionCreated created => new CreateSessionResponse { Data = ToMessageSessionCreated(created) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<RunTurnResponse> RunTurnAsync(
        RunTurnRequest request,
        ServerCallContext context)
    {
        var body = request.Body ?? throw EvidenceGrpcMapping.Missing("body");

        var result = await _operations
            .RunTurnAsync(
                request.SessionId,
                new WireTurnRequest(body.Query, body.TopK, body.Complete, body.ResearchProfile),
                cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireTurnResponse turn => new RunTurnResponse { Data = ToMessageTurn(turn) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <summary>
    /// Refuses the streamed turn by name, which is what the original's own protobuf does with it.
    /// </summary>
    /// <remarks>
    /// The original streams a turn over SSE and gives it no gRPC twin: its session service carries the unary turn and
    /// nothing that streams, because the progress events are a shape the stream is defined by rather than a message.
    /// Answering with one event would be worse than refusing - it would look like a turn that reported a single stage -
    /// and pretending to stream it unary would look like a turn that never reported any. So the call names the surface
    /// that does serve it.
    /// </remarks>
    /// <param name="request">The request.</param>
    /// <param name="context">The call context.</param>
    /// <returns>Never; the call is refused.</returns>
    public override Task<StreamTurnResponse> StreamTurnAsync(
        StreamTurnRequest request,
        ServerCallContext context) =>
        throw new RpcException(new Status(
            StatusCode.Unimplemented,
            "the streamed turn is JSON-only, because the original streams it over SSE and its protobuf has no twin for "
                + "it. Watch it over HTTP at POST /v1/sessions/{session_id}/turns/stream."));

    /// <inheritdoc />
    public override async Task<GetSessionResponse> GetSessionAsync(
        GetSessionRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .GetSessionAsync(request.SessionId, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireSession session => new GetSessionResponse { Data = ToMessageSession(session) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<CloseSessionResponse> CloseSessionAsync(
        CloseSessionRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .CloseSessionAsync(request.SessionId, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireSessionClosed closed => new CloseSessionResponse
            {
                Data = new SessionClosed { SessionId = closed.SessionId, State = closed.State },
            },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ApplyRunbookResponse> ApplyRunbookAsync(
        ApplyRunbookRequest request,
        ServerCallContext context)
    {
        var declared = request.Body?.Yaml ?? throw EvidenceGrpcMapping.Missing("yaml");

        var result = await _operations
            .ApplyRunbookAsync(new WireRunbookApply(declared), context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireAppliedRunbook applied => new ApplyRunbookResponse { Data = ToMessageAppliedRunbook(applied) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<ListRunbooksResponse> ListRunbooksAsync(
        ListRunbooksRequest request,
        ServerCallContext context)
    {
        var listed = await _operations
            .ListRunbooksAsync(request.IncludeRemoved, context.CancellationToken)
            .ConfigureAwait(false);

        var message = new RunbookList();

        foreach (var runbook in listed.Runbooks)
        {
            message.Runbooks.Add(ToMessageRunbookVersion(runbook));
        }

        return new ListRunbooksResponse { Data = message };
    }

    /// <summary>Maps an opened session onto the contract's message.</summary>
    /// <param name="created">The session.</param>
    /// <returns>The message.</returns>
    private static SessionCreated ToMessageSessionCreated(WireSessionCreated created)
    {
        var message = new SessionCreated { SessionId = created.SessionId, RunbookRef = created.RunbookRef };

        message.PermittedCollections.AddRange(created.PermittedCollections);

        return message;
    }

    /// <summary>Maps a turn onto the contract's message.</summary>
    /// <param name="turn">The turn.</param>
    /// <returns>The message.</returns>
    private static TurnResponse ToMessageTurn(WireTurnResponse turn)
    {
        var message = new TurnResponse
        {
            Ordinal = turn.Ordinal,
            Query = turn.Query,
            IntentKind = turn.IntentKind ?? string.Empty,
            IntentExplicit = turn.IntentExplicit,
        };

        message.CollectionsSearched.AddRange(turn.CollectionsSearched);

        foreach (var hit in turn.Hits)
        {
            message.Hits.Add(new TurnHit
            {
                ChunkId = hit.ChunkId,
                SourcePath = hit.SourcePath,
                Score = hit.Score,
                Text = hit.Text,
            });
        }

        foreach (var envelope in turn.Envelopes)
        {
            var wire = new TurnEnvelope
            {
                IndexVersion = envelope.IndexVersion,
                LedgerWatermark = envelope.LedgerWatermark,
            };

            foreach (var source in envelope.Sources)
            {
                wire.Sources.Add(new TurnSource
                {
                    ChunkId = source.ChunkId,
                    SourcePath = source.SourcePath,
                    ContentHash = source.ContentHash,
                });
            }

            message.Envelopes.Add(wire);
        }

        // This generator's message-typed fields are never null, so absence is expressed the way every other absent
        // message in this contract is: as an empty one. A completion only reaches here when one was produced, so the
        // empty message means "none ran" rather than "one ran and said nothing".
        message.Completion = turn.Completion is null ? new TurnCompletion() : ToMessageCompletion(turn.Completion);
        message.Hierarchy = turn.Hierarchy is null ? new HierarchyDecision() : ToMessageHierarchy(turn.Hierarchy);

        return message;
    }

    private static TurnCompletion ToMessageCompletion(WireTurnCompletion completion)
    {
        var verification = new TurnVerification { Retries = completion.Verification.Retries };

        verification.Checks.AddRange(completion.Verification.Checks);
        verification.FirstPassViolations.AddRange(completion.Verification.FirstPassViolations);
        verification.Violations.AddRange(completion.Verification.Violations);

        return new TurnCompletion
        {
            Text = completion.Text,
            InputTokens = completion.InputTokens,
            OutputTokens = completion.OutputTokens,
            Completions = completion.Completions,
            RetriedForTruncation = completion.RetriedForTruncation,
            Verification = verification,
        };
    }

    private static HierarchyDecision ToMessageHierarchy(WireHierarchyDecision decision)
    {
        var message = new HierarchyDecision
        {
            Profile = decision.Profile,
            IntentKind = decision.IntentKind ?? string.Empty,
            IntentExplicit = decision.IntentExplicit,
            CompletenessAvailable = decision.CompletenessAvailable,
            DisclosedConflicts = decision.DisclosedConflicts,
            ConflictsPolicy = decision.ConflictsPolicy,
        };

        foreach (var layer in decision.Layers)
        {
            message.Layers.Add(new LayerOutcome
            {
                Layer = layer.Layer,
                Role = layer.Role,
                Requirement = layer.Requirement,
                Block = layer.Block,
                EvidenceId = layer.EvidenceId ?? string.Empty,
                SupportsCompleteness = layer.SupportsCompleteness,
                RefusalCode = layer.RefusalCode ?? string.Empty,
                ElapsedMs = layer.ElapsedMs,
            });
        }

        return message;
    }

    /// <summary>Maps a session and its transcript onto the contract's message.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The message.</returns>
    private static SessionResponse ToMessageSession(WireSession session)
    {
        var message = new SessionResponse
        {
            SessionId = session.SessionId,
            Uid = session.Uid,
            RunbookRef = session.RunbookRef,
            AccessLevel = session.AccessLevel,
            State = session.State,
            CreatedAt = session.CreatedAt ?? string.Empty,
            LastTurnAt = session.LastTurnAt ?? string.Empty,
        };

        message.Compartments.AddRange(session.Compartments);

        foreach (var turn in session.Turns)
        {
            var wire = new SessionTurn
            {
                Ordinal = turn.Ordinal,
                Query = turn.Query,
                Hits = turn.Hits,
                Envelope = turn.Envelope,
                Completion = turn.Completion ?? string.Empty,
                Hierarchy = turn.Hierarchy ?? string.Empty,
                CreatedAt = turn.CreatedAt ?? string.Empty,
            };

            wire.CollectionsSearched.AddRange(turn.CollectionsSearched);

            message.Turns.Add(wire);
        }

        return message;
    }

    /// <summary>Maps an applied version onto the contract's message.</summary>
    /// <param name="applied">The version.</param>
    /// <returns>The message.</returns>
    private static AppliedRunbook ToMessageAppliedRunbook(WireAppliedRunbook applied) => new()
    {
        RunbookRef = applied.RunbookRef,
        Name = applied.Name,
        Version = applied.Version,
        Status = StatusOf(applied.Status),
        CreatedAt = applied.CreatedAt ?? string.Empty,
        UpdatedAt = applied.UpdatedAt ?? string.Empty,
    };

    /// <summary>
    /// Maps a runbook status name onto the contract's enumeration.
    /// </summary>
    /// <remarks>
    /// Written out member by member rather than cast from one numbering to the other, like every other enumeration this
    /// adapter crosses: a cast would follow whichever numbering each side happens to use, so a renumbering on either side
    /// would silently change what a caller reads.
    /// </remarks>
    /// <param name="status">The status name the kernel reports.</param>
    /// <returns>The message's member.</returns>
    private static StatusEnum StatusOf(string status) => status switch
    {
        "remove_requested" => StatusEnum.RemoveRequested,
        "removed" => StatusEnum.Removed,
        _ => StatusEnum.Active,
    };

    /// <summary>Maps a listed version onto the contract's message.</summary>
    /// <param name="runbook">The version.</param>
    /// <returns>The message.</returns>
    private static RunbookVersion ToMessageRunbookVersion(WireRunbook runbook) => new()
    {
        RunbookRef = runbook.RunbookRef,
        Name = runbook.Name,
        Version = runbook.Version,
        Status = StatusOf(runbook.Status),
        RemovalId = runbook.RemovalId ?? string.Empty,
        RemovalRequestedAt = runbook.RemovalRequestedAt ?? string.Empty,
        RemovalRequestedBy = runbook.RemovalRequestedBy ?? string.Empty,
        RemovedAt = runbook.RemovedAt ?? string.Empty,
        CreatedAt = runbook.CreatedAt ?? string.Empty,
        UpdatedAt = runbook.UpdatedAt ?? string.Empty,
    };

    /// <inheritdoc />
    public override async Task<SealEvidenceResponse> SealEvidenceAsync(
        SealEvidenceRequest request,
        ServerCallContext context)
    {
        var declared = request.Body?.Manifest ?? throw EvidenceGrpcMapping.Missing("manifest");

        var result = await _operations
            .SealEvidenceAsync(
                new WireSealEvidenceRequest(EvidenceGrpcMapping.ToEvidence(declared), request.Body.BytesBase64),
                await PrincipalAsync(context, AccessScope.Evidence),
                context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireSealResponse sealedEvidence => new SealEvidenceResponse { Data = EvidenceGrpcMapping.ToMessage(sealedEvidence) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<PutEvidenceBytesResponse> PutEvidenceBytesAsync(
        PutEvidenceBytesRequest request,
        ServerCallContext context)
    {
        if (Decode(request.Body?.BytesBase64) is not { } bytes)
        {
            throw Problem(new WireProblem(
                MunariumOperations.InvalidRequestProblem,
                "bytes_base64 is required and has to be base64.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0));
        }

        var refusal = await _operations
            .PutEvidenceBytesAsync(
                await PrincipalAsync(context, AccessScope.Evidence),
                request.EvidenceId,
                request.Grant,
                bytes,
                context.CancellationToken)
            .ConfigureAwait(false);

        return refusal is null ? new PutEvidenceBytesResponse() : throw Problem(refusal);
    }

    /// <inheritdoc />
    public override async Task<CommitEvidenceResponse> CommitEvidenceAsync(
        CommitEvidenceRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .CommitEvidenceAsync(await PrincipalAsync(context, AccessScope.Evidence), request.EvidenceId, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireEvidenceCommit committed => new CommitEvidenceResponse { Data = EvidenceGrpcMapping.ToMessage(committed) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<GetEvidenceManifestResponse> GetEvidenceManifestAsync(
        GetEvidenceManifestRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .ReadEvidenceManifestAsync(await PrincipalAsync(context, AccessScope.Evidence), request.EvidenceId, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            Evidence.EvidenceManifest manifest => new GetEvidenceManifestResponse { Data = EvidenceGrpcMapping.ToMessage(manifest) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <summary>
    /// Refuses the row read by name, which is what the original does with it and what this transport has to do.
    /// </summary>
    /// <remarks>
    /// A row is keyed by the column names a manifest declares, and a protobuf map cannot carry a null value - so a row
    /// carried over gRPC would have to collapse the difference between an empty value and no value, which is precisely
    /// the distinction this plane exists to keep. Rather than answer with rows that lost a value, or with an empty list
    /// that looks like a table nobody wrote, the call says it is not served here and names where it is.
    /// </remarks>
    public override Task<GetEvidenceRowsResponse> GetEvidenceRowsAsync(
        GetEvidenceRowsRequest request,
        ServerCallContext context) =>
        throw new RpcException(new Status(
            StatusCode.Unimplemented,
            "the row read is JSON-only: a row keyed by the column names a manifest declares has no faithful protobuf "
                + "form. Read it over HTTP at GET /v1/evidence/{evidence_id}/rows."));

    /// <inheritdoc />
    public override async Task<ListEvidenceAccessesResponse> ListEvidenceAccessesAsync(
        ListEvidenceAccessesRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .ReadEvidenceAccessesAsync(
                MunariumKernel.Tenant, request.EvidenceId, request.Limit, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireEvidenceAccessList accesses => EvidenceGrpcMapping.ToMessage(accesses),
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<PurgeEvidenceResponse> PurgeEvidenceAsync(
        PurgeEvidenceRequest request,
        ServerCallContext context)
    {
        var result = await _operations
            .PurgeEvidenceAsync(MunariumKernel.Tenant, request.EvidenceId, context.CancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WireEvidencePurge purged => new PurgeEvidenceResponse { Data = EvidenceGrpcMapping.ToMessage(purged) },
            WireProblem problem => throw Problem(problem),
        };
    }

    /// <inheritdoc />
    public override async Task<SetEvidenceLegalHoldResponse> SetEvidenceLegalHoldAsync(
        SetEvidenceLegalHoldRequest request,
        ServerCallContext context)
    {
        var refusal = await _operations
            .SetEvidenceLegalHoldAsync(
                MunariumKernel.Tenant, request.EvidenceId, request.Body?.Hold ?? false, context.CancellationToken)
            .ConfigureAwait(false);

        return refusal is null ? new SetEvidenceLegalHoldResponse() : throw Problem(refusal);
    }

    /// <summary>
    /// Decodes the bytes a caller sent, or nothing when they did not send usable ones.
    /// </summary>
    /// <remarks>
    /// Base64 rather than the octet stream the original takes, because this port's surfaces are JSON by design and the
    /// canonical form includes Parquet, which is not text. Something that is not base64 is refused before the artifact
    /// is looked at, so a malformed upload never spends a grant.
    /// </remarks>
    private static byte[]? Decode(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return null;
        }

        var buffer = new byte[(value.Length / 4 * 3) + 3];

        return Convert.TryFromBase64String(value, buffer, out var written)
            ? buffer[..written]
            : null;
    }


    /// <summary>Reads the catalog as the messages the specification declares.</summary>
    /// <param name="patterns">The catalog.</param>
    /// <returns>The message.</returns>
    private static AuthoringPatternList ToMessage(WireAuthoringPatternList patterns)
    {
        var list = new AuthoringPatternList();

        list.Patterns.AddRange(patterns.Patterns.Select(ToMessage));

        return list;
    }

    /// <summary>Reads one pattern as the message.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The message.</returns>
    private static AuthoringPattern ToMessage(WireAuthoringPattern pattern)
    {
        var message = new AuthoringPattern
        {
            Id = pattern.Id,
            Name = pattern.Name,
            Description = pattern.Description,
            StartFrom = pattern.StartFrom,
            Guidance = pattern.Guidance,
            HasCompletion = pattern.HasCompletion,
        };

        message.ShapeNames.AddRange(pattern.ShapeNames);
        message.DecisionNotes.AddRange(pattern.DecisionNotes);

        return message;
    }

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

    private static IngestedSource ToMessage(WireIngestedSource ingested) => new()
    {
        SourceId = ingested.SourceId,
        Path = ingested.Path,
        Kind = ingested.Kind,
        MediaType = ingested.MediaType,
        ContentHash = ingested.ContentHash,
        Bytes = ingested.Bytes,
        BlobUri = ingested.BlobUri,
        BackendId = ingested.BackendId,
        IngestedAt = ingested.IngestedAt ?? string.Empty,
        ChunksIndexed = ingested.ChunksIndexed,
        IndexVersion = ingested.IndexVersion,
    };

    private static SourceInfo ToMessage(WireSourceInfo info) => new()
    {
        SourceId = info.SourceId,
        Path = info.Path,
        MediaType = info.MediaType,
        ContentHash = info.ContentHash,
        Bytes = info.Bytes,
        BlobUri = info.BlobUri,
        BackendId = info.BackendId,
        IngestedAt = info.IngestedAt ?? string.Empty,
        ExtractionStatus = info.ExtractionStatus ?? string.Empty,
        ExtractionMethod = info.ExtractionMethod ?? string.Empty,
    };

    private static IndexManifest ToMessage(WireIndexManifest manifest) => new()
    {
        CollectionId = manifest.CollectionId,
        CollectionName = manifest.CollectionName ?? string.Empty,
        ShapeRef = manifest.ShapeRef ?? string.Empty,
        Engine = manifest.Engine,
        Chunker = manifest.Chunker,
        Extractors = manifest.Extractors,
        Embedder = manifest.Embedder,
        MaxChars = manifest.MaxChars,
        SourceContentHashes = { manifest.SourceContentHashes },
    };

    private static IndexVersionState ToMessage(WireIndexVersion version) => new()
    {
        IndexVersionId = version.IndexVersionId,
        CollectionId = version.CollectionId,
        ShapeRef = version.ShapeRef,
        Watermark = version.Watermark,
        Active = version.Active,
        Superseded = version.Superseded,
        ActivatedAt = version.ActivatedAt ?? string.Empty,
        DeactivatedAt = version.DeactivatedAt ?? string.Empty,
        Manifest = ToMessage(version.Manifest),
    };

    private static EnvelopeResolution ToMessage(WireEnvelopeResolution resolution)
    {
        var message = new EnvelopeResolution
        {
            Resolved = resolution.Resolved,
            Failure = resolution.Failure ?? string.Empty,
            UnrecordedContentHashes = { resolution.UnrecordedContentHashes },
        };

        // An absent version is an absent field rather than a null message: the contract marks it optional, and a
        // resolution that failed to find one is exactly what "no version" means.
        if (resolution.Version is not null)
        {
            message.Version = ToMessage(resolution.Version);
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

    // The inbound direction of a citation, which only the envelope resolution needs: every other read sends sources out.
    private static WireSourceReference ToWire(SourceReference source) =>
        new(source.ChunkId, source.SourceId, source.SourcePath, source.ContentHash, source.ChunkOrdinal);

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

        return new WireClaimBatchRequest(
            claims,
            body?.Text ?? string.Empty,
            body?.ExpectedHead ?? 0,
            body?.IdempotencyKey ?? string.Empty);
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
        proposal?.Actor ?? string.Empty,
        proposal?.IdempotencyKey ?? string.Empty);
}
