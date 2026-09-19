namespace Munarium.Sessions;

using Munarium.Evidence;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Runbooks;

/// <summary>The model each paid step of a turn uses, already resolved by the deployment.</summary>
/// <param name="Intent">The model the intent task classifies with.</param>
/// <param name="Expansion">The model the query-expansion task widens with.</param>
/// <param name="Completion">The model that answers.</param>
public sealed record TurnModels(string Intent, string Expansion, string Completion);

/// <summary>What one turn of a session produced.</summary>
public sealed record SessionTurnExecuted
{
    /// <summary>Gets the ordinal the store gave the turn.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Gets the question as asked.</summary>
    public required string Question { get; init; }

    /// <summary>Gets what the question was understood to be asking.</summary>
    public required QueryIntent Intent { get; init; }

    /// <summary>Gets the collections the turn searched, after access filtering.</summary>
    public required IReadOnlyList<string> CollectionsSearched { get; init; }

    /// <summary>Gets the merged hits and their provenance.</summary>
    public required RetrievalResult Hits { get; init; }

    /// <summary>Gets the completion and its verification, when a model answered.</summary>
    public TurnOutcome? Completion { get; init; }

    /// <summary>Gets the hierarchy's decision, when a research profile ran.</summary>
    public EvidenceHierarchyDecision? Decision { get; init; }
}

/// <summary>
/// The result of a turn: what it produced, or why nothing was produced.
/// </summary>
/// <remarks>
/// Four cases rather than an exception, for the same reason the runner's own result is a union: a closed session, an
/// unknown profile and a required layer that could not answer are all answers a caller has to disclose, and each is more
/// useful to a caller than a 500.
/// </remarks>
public readonly union SessionTurnResult(
    SessionTurnExecuted,
    SessionRefusal,
    ResearchProblem,
    RequiredLayerUnavailable);

/// <summary>
/// Runs one turn of a session: what it was asking, what it was allowed to see, what answered, and what is recorded.
/// </summary>
/// <remarks>
/// The order is the design. A closed session is refused before anything is read. Then the intent, because the plan a
/// turn runs under is a function of the question and not of the caller's patience. Then the evidence, and only then a
/// model - a required layer that cannot answer stops the turn before a completion is paid for. Finally the turn is
/// recorded, decision included, so "why did the model see this?" is answerable for every turn rather than only for the
/// ones small enough to keep.
/// <para>
/// The retrieval is the deployment's serving index, searched once: this port builds and serves one index per process, so
/// a permitted collection with no index of its own is a limitation of the index plane rather than of the session plane.
/// The collections recorded are the ones the clearance permitted, which is what the original records for the collections
/// it searched.
/// </para>
/// </remarks>
/// <param name="sessions">Where the turn is recorded.</param>
/// <param name="index">The index that answers.</param>
/// <param name="embedder">The model that embeds the question.</param>
/// <param name="model">The model that classifies and answers.</param>
/// <param name="embeddingModel">The embedding model to ask for.</param>
/// <param name="providers">The evidence providers, in trust order.</param>
/// <param name="tenant">The deployment's tenant.</param>
public sealed class SessionTurnRunner(
    ISessionStore sessions,
    IIndexHost index,
    IModelProvider embedder,
    IModelProvider model,
    string embeddingModel,
    IReadOnlyList<IEvidenceProvider> providers,
    string tenant)
{
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IIndexHost _index = index ?? throw new ArgumentNullException(nameof(index));
    private readonly IModelProvider _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    private readonly IModelProvider _model = model ?? throw new ArgumentNullException(nameof(model));
    private readonly string _embeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));
    private readonly IReadOnlyList<IEvidenceProvider> _providers =
        providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly string _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));

    /// <summary>
    /// The completion ceiling a runbook that names none gets.
    /// </summary>
    /// <remarks>
    /// The original's built-in turn ceiling is two thousand and forty-eight, and it is a ceiling rather than spend: a
    /// runbook that needs a longer answer should say so, and one that says nothing should not be able to buy an
    /// arbitrarily large one by accident.
    /// </remarks>
    public const int DefaultCompletionMaxTokens = 2048;

    /// <summary>The chunk ceiling a turn gets when neither the caller nor the runbook names one.</summary>
    public const int DefaultTopK = 10;

    /// <summary>
    /// Runs a turn.
    /// </summary>
    /// <param name="session">The session, which has to be open.</param>
    /// <param name="document">The runbook the session pinned.</param>
    /// <param name="question">The question as asked.</param>
    /// <param name="requestedProfile">The profile the caller named, if any.</param>
    /// <param name="models">The models each paid step uses.</param>
    /// <param name="complete">Whether the runbook's completion step runs, when it declares one.</param>
    /// <param name="topK">How many chunks the answer may carry, or zero for the runbook's own.</param>
    /// <param name="onProgress">An optional listener; the turn's result never depends on one being present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the turn produced, or why nothing was produced.</returns>
    public async ValueTask<SessionTurnResult> RunAsync(
        SessionRecord session,
        RunbookDocument document,
        string question,
        string? requestedProfile,
        TurnModels models,
        bool complete,
        int topK = 0,
        Action<TurnProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        if (session.State is not SessionState.Open)
        {
            return new SessionRefusal(
                SessionRefusalCodes.SessionClosed,
                $"session '{session.Id}' is {session.State.ToWireName()} and accepts no further turns");
        }

        var intent = await IntentResolution
            .ResolveAsync(document, question, _model, models.Intent, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var (profile, problem) = ResearchProfiles.Resolve(
            document.Spec.Retrieval?.ResearchProfiles ?? [],
            requestedProfile,
            document.Spec.Retrieval?.DefaultResearchProfile);

        return problem is not null
            ? problem
            : await AnswerAsync(session, document, question, intent, profile, models, complete, topK, onProgress, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Gathers the evidence, asks the model when the turn asked for an answer, and records the turn.
    /// </summary>
    private async ValueTask<SessionTurnResult> AnswerAsync(
        SessionRecord session,
        RunbookDocument document,
        string question,
        QueryIntent intent,
        ResearchProfile? profile,
        TurnModels models,
        bool complete,
        int topK,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        // The clearance is the session's rather than the caller's, which is the whole point of the snapshot: a turn
        // reads what the conversation was opened to read.
        var searched = SessionCreation.PermittedCollections(document, session.Access);
        var plan = profile is null ? null : ResearchProfiles.BuildPlan(profile, intent);
        var request = Completion(document, complete, question);
        RetrievalResult? hits = null;

        // Which model is about to answer is known before anything is paid for, which is the only moment a stream can
        // say it. A turn that runs no completion resolves none, so it reports none.
        if (request is not null)
        {
            onProgress?.Invoke(new TurnModelResolved(_model.Id.Value, models.Completion, Tier: null, WasOverride: false));
        }

        // One search per turn, however many layers ask for it: the evidence hierarchy composes what the retrieval
        // returned, it does not retrieve once per layer.
        async ValueTask<RetrievalResult> SearchOnceAsync(CancellationToken token)
        {
            if (hits is { } already) return already;

            var widened = await WidenAsync(document, question, models.Expansion, onProgress, token)
                .ConfigureAwait(false);

            hits = await SearchAsync(document, widened, topK, token).ConfigureAwait(false);

            // Reported where the retrieval returns rather than where the turn reads it: that is the boundary, and a
            // layer that asks second is served the same search rather than a second one.
            onProgress?.Invoke(new TurnMerged(hits.Chunks.Count));

            return hits;
        }

        TurnOutcome? outcome = null;
        EvidenceHierarchyDecision? decision = null;

        if (plan is not null)
        {
            if (request is null)
            {
                // Evidence without an answer: the hierarchy still decides, and the decision is still what the turn
                // records, because the caller who asked for hits only may ask why it got those.
                var run = await HierarchyRunner
                    .ExecuteAsync(
                        plan,
                        _providers,
                        (_, token) => SearchOnceAsync(token),
                        hierarchy => onProgress?.Invoke(hierarchy),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (run is RequiredLayerUnavailable refusal)
                {
                    return refusal;
                }

                if (run is HierarchyOutcome hierarchy)
                {
                    decision = hierarchy.Decision;
                }
            }
            else
            {
                var result = await TurnPipeline
                    .ExecuteAsync(
                        plan,
                        request,
                        _providers,
                        (_, token) => SearchOnceAsync(token),
                        Labels,
                        _model,
                        models.Completion,
                        document.Spec.Completion?.ContextCharBudget ?? TurnPipeline.DefaultContextBudget,
                        onProgress,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (result is RequiredLayerUnavailable refusal)
                {
                    return refusal;
                }

                if (result is TurnOutcome answered)
                {
                    outcome = answered;
                    decision = answered.Decision;
                }
            }
        }

        var retrieved = await SearchOnceAsync(cancellationToken).ConfigureAwait(false);

        if (outcome is null && request is not null)
        {
            outcome = await AnswerOverHitsAsync(request, retrieved, models, onProgress, cancellationToken)
                .ConfigureAwait(false);
        }

        var ordinal = await _sessions
            .AppendTurnAsync(
                new TurnRecord
                {
                    Tenant = _tenant,
                    SessionId = session.Id,
                    Uid = session.Uid,
                    Query = question,
                    CollectionsSearched = searched,
                    HitsJson = SessionTurnPayloads.Hits(retrieved),
                    EnvelopeJson = SessionTurnPayloads.Envelopes(retrieved),
                    CompletionJson = outcome is null ? null : SessionTurnPayloads.Completion(outcome),
                    HierarchyJson = decision is null ? null : SessionTurnPayloads.Hierarchy(decision),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new SessionTurnExecuted
        {
            Ordinal = ordinal,
            Question = question,
            Intent = intent,
            CollectionsSearched = searched,
            Hits = retrieved,
            Completion = outcome,
            Decision = decision,
        };
    }

    /// <summary>
    /// Builds the completion request, or nothing when this turn runs no completion.
    /// </summary>
    /// <remarks>
    /// The caller's <c>complete</c> flag and the runbook's own completion are both required, because either one alone
    /// would mean answering a question the caller did not ask: a runbook that declares a completion does not have to
    /// spend it on every turn, and asking for one of a runbook that declares none is not a request this can honour.
    /// </remarks>
    /// <param name="document">The runbook.</param>
    /// <param name="complete">Whether the caller asked for an answer.</param>
    /// <param name="question">The question, which the prompt's placeholders are filled with.</param>
    /// <returns>The request, or <see langword="null"/>.</returns>
    private static TurnRequest? Completion(RunbookDocument document, bool complete, string question) =>
        complete && document.Spec.Completion is { } spec
            ? new TurnRequest(
                question,
                spec.PromptTemplate,
                spec.MaxTokens ?? DefaultCompletionMaxTokens,
                new TurnVerificationChecks(
                    spec.Verification?.Quotes ?? false,
                    spec.Verification?.Citations ?? false,
                    spec.Verification?.MaxRetries ?? 1))
            : null;

    /// <summary>
    /// Widens the question through the runbook's query-expansion step, when it declares one.
    /// </summary>
    /// <remarks>
    /// The step is optional, and the runbook decides whether a failure is fatal: a declared step that fails falls back to
    /// the question as asked, and one the runbook marks <c>required</c> fails the turn - because a caller who asked for a
    /// widened search and silently got the narrow one is reading a different search than the one they configured. Nothing
    /// is reported when nothing happened: a failure the runbook tolerates produces no event, so a stream cannot show an
    /// expansion that did not run.
    /// </remarks>
    /// <param name="document">The runbook.</param>
    /// <param name="question">The question as asked.</param>
    /// <param name="modelId">The model to widen with.</param>
    /// <param name="onProgress">An optional listener.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text to search with.</returns>
    private async ValueTask<string> WidenAsync(
        RunbookDocument document,
        string question,
        string modelId,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var result = await QueryExpansion
            .ResolveAsync(document, question, _model, modelId, cancellationToken)
            .ConfigureAwait(false);

        switch (result)
        {
            case QueryExpansionApplied applied:
                onProgress?.Invoke(new TurnExpanded(
                    applied.Provider,
                    applied.Model,
                    applied.Terms,
                    applied.InputTokens,
                    applied.OutputTokens));

                return QueryExpansion.Widen(question, applied.Terms);

            case QueryExpansionUnavailable unavailable
                when document.Spec.Retrieval?.ModelQueryExpansion?.Required is true:
                throw new InvalidOperationException(
                    $"the runbook requires a query expansion and it produced nothing: {unavailable.Reason}");

            default:
                return question;
        }
    }

    /// <summary>
    /// Searches the deployment's serving index, in the runbook's own words.
    /// </summary>
    /// <remarks>
    /// The version that answers is read at the moment of use rather than held: a cutover is a serving decision, and a
    /// turn that captured a reader would keep asking the version that stopped serving.
    /// </remarks>
    /// <param name="document">The runbook.</param>
    /// <param name="question">The question.</param>
    /// <param name="topK">The caller's override, or zero for the runbook's own.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chunks and their provenance.</returns>
    private async ValueTask<RetrievalResult> SearchAsync(
        RunbookDocument document,
        string question,
        int topK,
        CancellationToken cancellationToken)
    {
        var embedded = await _embedder
            .EmbedAsync(new EmbeddingRequest { Model = _embeddingModel, Inputs = [question] }, cancellationToken)
            .ConfigureAwait(false);

        var wanted = topK > 0 ? topK : document.Spec.Retrieval?.TopK ?? DefaultTopK;

        return await _index.ServingReader
            .SearchAsync(
                new RetrievalQuery { Text = question, Embedding = embedded.Vectors[0], TopK = wanted },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Answers over the merged hits, when no research profile ran.
    /// </summary>
    /// <remarks>
    /// The document path's completion: the hits are rendered as the labelled blocks the answer may quote from, and the
    /// labels it may cite are the chunk labels the context prints and the paths the hits came from - both, because an
    /// answer may name either and neither is a guess.
    /// </remarks>
    /// <param name="request">What the turn is asked to do.</param>
    /// <param name="hits">The merged hits.</param>
    /// <param name="models">The models each paid step uses.</param>
    /// <param name="onProgress">An optional listener; the turn's result never depends on one being present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer and what it cost.</returns>
    private async ValueTask<TurnOutcome> AnswerOverHitsAsync(
        TurnRequest request,
        RetrievalResult hits,
        TurnModels models,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken) =>
        await TurnPipeline
            .AnswerOverTextAsync(
                request,
                RenderHits(hits),
                [.. hits.Chunks.Select(chunk => chunk.Text)],
                Labels(hits),
                _model,
                models.Completion,
                onProgress,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The labels an answer may cite out of a retrieval.</summary>
    /// <param name="hits">The retrieval.</param>
    /// <returns>The citable labels.</returns>
    private static IReadOnlyList<string> Labels(RetrievalResult hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        return
        [
            .. hits.Chunks.Select(chunk => chunk.Source.ChunkId),
            .. hits.Chunks.Select(chunk => chunk.Source.SourcePath),
        ];
    }

    /// <summary>Renders the retrieved chunks as the labelled blocks a prompt carries.</summary>
    /// <param name="hits">The retrieval.</param>
    /// <returns>The rendered context.</returns>
    private static string RenderHits(RetrievalResult hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        return string.Join("\n\n", hits.Chunks.Select(chunk => $"[{chunk.Source.ChunkId}] {chunk.Text}"));
    }
}
