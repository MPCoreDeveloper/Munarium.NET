namespace Munarium.Sessions;

using Munarium.Budgets;
using Munarium.Evidence;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Runbooks;

/// <summary>
/// What one turn is asked to do, and which session is asking.
/// </summary>
/// <remarks>
/// Gathered rather than passed one at a time, because a turn is one request: the session it runs in, the runbook that
/// session pinned, the question, and what the caller wants done with it. Every turn carries the same things, and a
/// caller that supplied one of them for a different session would be answering a question nobody asked.
/// </remarks>
public sealed record SessionTurnRequest
{
    /// <summary>Gets the session, which has to be open.</summary>
    public required SessionRecord Session { get; init; }

    /// <summary>Gets the runbook the session pinned.</summary>
    public required RunbookDocument Document { get; init; }

    /// <summary>Gets the question as asked.</summary>
    public required string Question { get; init; }

    /// <summary>Gets the profile the caller named, if any.</summary>
    public string? RequestedProfile { get; init; }

    /// <summary>Gets the models each paid step uses.</summary>
    public required TurnModels Models { get; init; }

    /// <summary>Gets a value indicating whether the runbook's completion step runs, when it declares one.</summary>
    public bool Complete { get; init; }

    /// <summary>Gets how many chunks the answer may carry, or zero for the runbook's own.</summary>
    public int TopK { get; init; }

    /// <summary>Gets an optional listener; the turn's result never depends on one being present.</summary>
    public Action<TurnProgress>? OnProgress { get; init; }

    /// <summary>Gets the cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// What one search of a turn produced: the merged hits, and one envelope per collection that answered.
/// </summary>
/// <remarks>
/// The carrier between the fan-out and the turn, and deliberately not a <c>RetrievalResult</c>: that type pairs one
/// answer with one envelope, which is what a single index gives. A merge over several collections has several
/// envelopes and no single version to seal one over, so the two are kept apart here rather than one being chosen to
/// speak for the others.
/// </remarks>
/// <param name="Chunks">The merged hits, best first.</param>
/// <param name="Envelopes">One envelope per collection that answered, in the order they were searched.</param>
internal sealed record SessionTurnSearch(
    IReadOnlyList<RetrievedChunk> Chunks,
    IReadOnlyList<ProvenanceEnvelope> Envelopes)
{
    /// <summary>Wraps one index's answer, which is one envelope.</summary>
    /// <param name="result">What the index answered.</param>
    /// <returns>The search.</returns>
    public static SessionTurnSearch Of(RetrievalResult result) => new(result.Chunks, [result.Envelope]);

    /// <summary>
    /// The search as the one retrieval a hierarchy's document layer takes.
    /// </summary>
    /// <remarks>
    /// That layer reads the chunks - it renders them and labels them - while the turn's own provenance is
    /// <see cref="Envelopes"/>, recorded per collection. So the envelope here is a shape for a caller that needs one
    /// rather than a promise about this answer: the first collection's, or a version-less one when no collection answered
    /// at all, which is the honest envelope for an answer that came from no index. Nothing else should use this, because
    /// an envelope naming one collection's version while the answer came from several is exactly the claim this type
    /// exists to prevent.
    /// </remarks>
    /// <returns>The chunks with one envelope.</returns>
    public RetrievalResult AsOne() => new(
        Chunks,
        Envelopes.Count > 0 ? Envelopes[0] : new ProvenanceEnvelope(string.Empty, SequenceNumber.Zero, []));
}

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

    /// <summary>Gets the merged hits.</summary>
    public required IReadOnlyList<RetrievedChunk> Hits { get; init; }

    /// <summary>
    /// Gets one envelope per collection that answered, in the order they were searched.
    /// </summary>
    /// <remarks>
    /// Per collection rather than one over all of them, because an envelope is a promise about <em>one</em> index
    /// version: a turn over several collections has several, and each one says which corpus those chunks came from and
    /// which ledger position it was built at. A single envelope would have to choose one collection's version to speak
    /// for the rest, which is the kind of promise an envelope exists to avoid.
    /// </remarks>
    public required IReadOnlyList<ProvenanceEnvelope> Envelopes { get; init; }

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
/// <param name="settings">The deployment's serving configuration.</param>
public sealed class SessionTurnRunner(
    ISessionStore sessions,
    IIndexHost index,
    SessionTurnSettings settings)
{
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IIndexHost _index = index ?? throw new ArgumentNullException(nameof(index));
    private readonly IModelProvider _embedder =
        (settings ?? throw new ArgumentNullException(nameof(settings))).Embedder;
    private readonly IModelProvider _model = settings.Model;
    private readonly string _embeddingModel = settings.EmbeddingModel;
    private readonly IReadOnlyList<IEvidenceProvider> _providers = settings.Providers;
    private readonly string _tenant = settings.Tenant;

    /// <summary>
    /// Gets the collections this deployment can search one at a time, or <see langword="null"/> when it built one corpus.
    /// </summary>
    /// <remarks>
    /// Optional on purpose: a deployment with a single serving index is a deployment with one collection as far as a turn
    /// is concerned, and every turn before this seam existed worked that way. When it is present, a turn searches each
    /// permitted collection's own live version; when it is not, it searches the one that serves.
    /// </remarks>
    private readonly ICollectionIndexes? _collections = settings.Collections;
    private readonly MaxTokensCeiling? _ceiling = settings.Ceiling;

    /// <summary>
    /// The completion ceiling a runbook that names none gets, when the deployment composed none either.
    /// </summary>
    /// <remarks>
    /// It is <see cref="MaxTokensBudget.Builtin"/>'s <c>turn_completion</c>: two thousand and forty-eight, and a ceiling
    /// rather than spend - a runbook that needs a longer answer should say so, and one that says nothing should not be
    /// able to buy an arbitrarily large one by accident.
    /// </remarks>
    public static int DefaultCompletionMaxTokens => MaxTokensBudget.Builtin.TurnCompletion;

    /// <summary>Gets the chunk ceiling a turn gets when neither the caller nor the runbook names one.</summary>
    public const int DefaultTopK = 10;

    /// <summary>Reads the ceilings a turn's paid steps are held to.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ceilings that apply, or the built-ins when this runner was composed without a ceiling.</returns>
    private async ValueTask<MaxTokensBudget> CeilingAsync(CancellationToken cancellationToken) =>
        _ceiling is null
            ? MaxTokensBudget.Builtin
            : (await _ceiling.EffectiveAsync(_tenant, cancellationToken).ConfigureAwait(false)).Budgets;

    /// <summary>
    /// Runs a turn.
    /// </summary>
    /// <param name="request">What the turn is asked to do, and which session is asking.</param>
    /// <returns>What the turn produced, or why nothing was produced.</returns>
    public async ValueTask<SessionTurnResult> RunAsync(SessionTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);

        if (request.Session.State is not SessionState.Open)
        {
            return new SessionRefusal(
                SessionRefusalCodes.SessionClosed,
                $"session '{request.Session.Id}' is {request.Session.State.ToWireName()} and accepts no further turns");
        }

        var budgets = await CeilingAsync(request.CancellationToken).ConfigureAwait(false);

        var intent = await IntentResolution
            .ResolveAsync(
                request.Document,
                request.Question,
                _model,
                request.Models.Intent,
                maxTokens: budgets.HierarchyClassifier,
                cancellationToken: request.CancellationToken)
            .ConfigureAwait(false);

        var (profile, problem) = ResearchProfiles.Resolve(
            request.Document.Spec.Retrieval?.ResearchProfiles ?? [],
            request.RequestedProfile,
            request.Document.Spec.Retrieval?.DefaultResearchProfile);

        return problem is not null
            ? problem
            : await AnswerAsync(request, new ResolvedTurn(intent, profile, budgets)).ConfigureAwait(false);
    }

    /// <summary>What a turn resolved before any evidence was gathered.</summary>
    /// <param name="Intent">The intent the classifier resolved, or the pinned one when none was asked for.</param>
    /// <param name="Profile">The research profile the turn runs, or <see langword="null"/> when none was named.</param>
    /// <param name="Budgets">The deployment's ceilings, as this turn reads them.</param>
    private sealed record ResolvedTurn(QueryIntent Intent, ResearchProfile? Profile, MaxTokensBudget Budgets);

    /// <summary>What the evidence stage produced: a refusal that ends the turn, or an answer and the decision it made.</summary>
    /// <param name="Outcome">The answer, when a model was asked and answered it.</param>
    /// <param name="Decision">The hierarchy's decision, when one ran.</param>
    /// <param name="Refusal">The required layer that could not answer, which ends the turn.</param>
    private sealed record EvidenceStage(
        TurnOutcome? Outcome,
        EvidenceHierarchyDecision? Decision,
        SessionTurnResult? Refusal);

    /// <summary>
    /// One turn's search, made once however many layers ask for it.
    /// </summary>
    /// <remarks>
    /// The evidence hierarchy composes what the retrieval returned; it does not retrieve once per layer, so the first
    /// layer to ask makes the search and every later one is served the same result - and the merge is reported where the
    /// retrieval returns rather than where the turn reads it, because that is the boundary.
    /// </remarks>
    /// <param name="runner">The runner the search asks.</param>
    /// <param name="turn">What the turn is asked to do.</param>
    /// <param name="budgets">The ceilings the search is held to.</param>
    /// <param name="searched">The collections the clearance permits, in the runbook's order.</param>
    private sealed class TurnSearch(
        SessionTurnRunner runner,
        SessionTurnRequest turn,
        MaxTokensBudget budgets,
        IReadOnlyList<string> searched)
    {
        private SessionTurnSearch? _hits;

        /// <summary>Searches once, and answers the same result to every later caller.</summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The merged hits, and one envelope per collection that answered.</returns>
        public async ValueTask<SessionTurnSearch> OnceAsync(CancellationToken cancellationToken)
        {
            if (_hits is { } already)
            {
                return already;
            }

            var widened = await runner
                .WidenAsync(
                    turn.Document,
                    turn.Question,
                    turn.Models.Expansion,
                    budgets.QueryExpansion,
                    turn.OnProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            // The fan-out when the deployment has an index per collection, and the one serving index otherwise - which
            // is what a deployment that built a single corpus has, and what every turn did before collections could be
            // searched one at a time.
            _hits = runner._collections is null
                || !await runner.HasOwnIndexesAsync(searched, cancellationToken).ConfigureAwait(false)
                    ? SessionTurnSearch.Of(
                        await runner.SearchAsync(turn.Document, widened, turn.TopK, cancellationToken).ConfigureAwait(false))
                    : await runner
                        .SearchCollectionsAsync(
                            turn.Document,
                            searched,
                            turn.Question,
                            widened,
                            turn.TopK,
                            turn.OnProgress,
                            cancellationToken)
                        .ConfigureAwait(false);

            turn.OnProgress?.Invoke(new TurnMerged(_hits.Chunks.Count));

            return _hits;
        }

        /// <summary>What a document layer takes: one retrieval, whose chunks are the merged hits.</summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The retrieval.</returns>
        public async ValueTask<RetrievalResult> DocumentsAsync(CancellationToken cancellationToken) =>
            (await OnceAsync(cancellationToken).ConfigureAwait(false)).AsOne();
    }

    /// <summary>
    /// Runs the profile's hierarchy over the turn's search, or answers straight over it when the turn runs none.
    /// </summary>
    /// <remarks>
    /// Evidence first, then an answer, and the order is the design: a required layer that cannot answer stops the turn
    /// <em>before</em> a model is paid for, which is the one place a refusal is fatal rather than disclosed.
    /// </remarks>
    /// <param name="plan">The plan to execute, or <see langword="null"/> when no profile was named.</param>
    /// <param name="request">The completion request, or <see langword="null"/> when the turn runs no completion.</param>
    /// <param name="turn">What the turn is asked to do.</param>
    /// <param name="search">The turn's search, made once for however many layers ask.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer and the decision, or the refusal that ended the turn.</returns>
    private async ValueTask<EvidenceStage> EvidenceAsync(
        EvidencePlan? plan,
        TurnRequest? request,
        SessionTurnRequest turn,
        TurnSearch search,
        CancellationToken cancellationToken)
    {
        if (plan is null)
        {
            return new EvidenceStage(null, null, null);
        }

        if (request is null)
        {
            // Evidence without an answer: the hierarchy still decides, and the decision is still what the turn records,
            // because the caller who asked for hits only may ask why it got those.
            var run = await HierarchyRunner
                .ExecuteAsync(
                    plan,
                    _providers,
                    (_, token) => search.DocumentsAsync(token),
                    hierarchy => turn.OnProgress?.Invoke(hierarchy),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return run switch
            {
                RequiredLayerUnavailable refusal => new EvidenceStage(null, null, refusal),
                HierarchyOutcome hierarchy => new EvidenceStage(null, hierarchy.Decision, null),
                _ => new EvidenceStage(null, null, null),
            };
        }

        var answered = await TurnPipeline
            .ExecuteAsync(
                new TurnExecution
                {
                    Plan = plan,
                    Request = request,
                    Providers = _providers,
                    DocumentLayer = (_, token) => search.DocumentsAsync(token),
                    ServedLabels = served => Labels(served.Chunks),
                    Model = new AnsweringModel(_model, turn.Models.Completion),
                    ContextBudget = turn.Document.Spec.Completion?.ContextCharBudget
                        ?? TurnPipeline.DefaultContextBudget,
                    OnProgress = turn.OnProgress,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return answered switch
        {
            RequiredLayerUnavailable refusal => new EvidenceStage(null, null, refusal),
            TurnOutcome outcome => new EvidenceStage(outcome, outcome.Decision, null),
            _ => new EvidenceStage(null, null, null),
        };
    }

    /// <summary>
    /// Gathers the evidence, asks the model when the turn asked for an answer, and records the turn.
    /// </summary>
    /// <param name="turn">What the turn is asked to do.</param>
    /// <param name="resolved">What the turn resolved before any evidence was gathered.</param>
    /// <returns>What the turn produced, or why nothing was produced.</returns>
    private async ValueTask<SessionTurnResult> AnswerAsync(SessionTurnRequest turn, ResolvedTurn resolved)
    {
        var session = turn.Session;
        var document = turn.Document;
        var question = turn.Question;
        var cancellationToken = turn.CancellationToken;

        // The clearance is the session's rather than the caller's, which is the whole point of the snapshot: a turn
        // reads what the conversation was opened to read.
        var searched = SessionCreation.PermittedCollections(document, session.Access);
        var plan = resolved.Profile is null ? null : ResearchProfiles.BuildPlan(resolved.Profile, resolved.Intent);
        var request = Completion(document, turn.Complete, question, resolved.Budgets.TurnCompletion);
        var search = new TurnSearch(this, turn, resolved.Budgets, searched);

        // Which model is about to answer is known before anything is paid for, which is the only moment a stream can
        // say it. A turn that runs no completion resolves none, so it reports none.
        if (request is not null)
        {
            turn.OnProgress?.Invoke(
                new TurnModelResolved(_model.Id.Value, turn.Models.Completion, Tier: null, WasOverride: false));
        }

        var evidence = await EvidenceAsync(plan, request, turn, search, cancellationToken).ConfigureAwait(false);

        if (evidence.Refusal is { } refusal)
        {
            return refusal;
        }

        var outcome = evidence.Outcome;
        var decision = evidence.Decision;
        var retrieved = await search.OnceAsync(cancellationToken).ConfigureAwait(false);

        if (outcome is null && request is not null)
        {
            outcome = await AnswerOverHitsAsync(
                    request,
                    retrieved.Chunks,
                    turn.Models,
                    turn.OnProgress,
                    cancellationToken)
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
                    HitsJson = SessionTurnPayloads.Hits(retrieved.Chunks),
                    EnvelopeJson = SessionTurnPayloads.Envelopes(retrieved.Envelopes),
                    CompletionJson = outcome is null ? null : SessionTurnPayloads.Completion(outcome),
                    HierarchyJson = decision is null ? null : SessionTurnPayloads.Hierarchy(decision),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new SessionTurnExecuted
        {
            Ordinal = ordinal,
            Question = question,
            Intent = resolved.Intent,
            CollectionsSearched = searched,
            Hits = retrieved.Chunks,
            Envelopes = retrieved.Envelopes,
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
    /// <param name="maxTokens">The ceiling a runbook that names none gets, which is the deployment's own.</param>
    /// <returns>The request, or <see langword="null"/>.</returns>
    private static TurnRequest? Completion(RunbookDocument document, bool complete, string question, int maxTokens) =>
        complete && document.Spec.Completion is { } spec
            ? new TurnRequest(
                question,
                spec.PromptTemplate,
                spec.MaxTokens ?? maxTokens,
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
    /// <param name="maxTokens">The ceiling a runbook that names none gets, which is the deployment's own.</param>
    /// <param name="onProgress">An optional listener.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text to search with.</returns>
    private async ValueTask<string> WidenAsync(
        RunbookDocument document,
        string question,
        string modelId,
        int maxTokens,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var result = await QueryExpansion
            .ResolveAsync(document, question, _model, modelId, maxTokens, cancellationToken)
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

    /// <summary>Searches the deployment's serving index, in the runbook's own words.</summary>
    /// <remarks>
    /// The version that answers is read at the moment of use rather than held: a cutover is a serving decision, and a
    /// turn that captured a reader would keep asking the version that stopped serving.
    /// </remarks>
    /// <param name="document">The runbook.</param>
    /// <param name="text">The text to search with.</param>
    /// <param name="topK">The caller's override, or zero for the runbook's own.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chunks and their provenance.</returns>
    private async ValueTask<RetrievalResult> SearchAsync(
        RunbookDocument document,
        string text,
        int topK,
        CancellationToken cancellationToken) =>
        await AskAsync(
            _index.ServingReader,
            text,
            await EmbedAsync(text, cancellationToken).ConfigureAwait(false),
            Wanted(document, topK),
            cancellationToken).ConfigureAwait(false);

    /// <summary>How many chunks a turn asks a collection for.</summary>
    /// <param name="document">The runbook.</param>
    /// <param name="topK">The caller's override, or zero for the runbook's own.</param>
    /// <returns>The number of chunks.</returns>
    private static int Wanted(RunbookDocument document, int topK) =>
        topK > 0 ? topK : document.Spec.Retrieval?.TopK ?? DefaultTopK;

    /// <summary>Reports whether any of the permitted collections has an index of its own here.</summary>
    /// <remarks>
    /// The whole-turn fallback turns on this: a deployment that built one corpus has no per-collection version, and a turn
    /// over it searches the index that serves - which is what every turn did before collections could be searched one at a
    /// time. Doing the fallback per collection instead would answer for a collection out of an index that is not its own,
    /// which is the claim the probe's <c>skipped</c> exists to avoid making.
    /// </remarks>
    /// <param name="permitted">The collections the clearance permits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether at least one of them can be searched on its own.</returns>
    private async ValueTask<bool> HasOwnIndexesAsync(
        IReadOnlyList<string> permitted,
        CancellationToken cancellationToken)
    {
        foreach (var collection in permitted)
        {
            var reader = await _collections!
                .ReaderForAsync(collection, cancellationToken)
                .ConfigureAwait(false);

            if (reader is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Embeds one text.
    /// </summary>
    /// <remarks>
    /// Separate from the search because the probe fan-out shares one embedding across every collection it probes, which
    /// is the original's own reason for preparing a probe once: N collections, one embedding, and one for the widened
    /// question when the deep search runs.
    /// </remarks>
    /// <param name="text">The text to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query vector.</returns>
    private async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var embedded = await _embedder
            .EmbedAsync(new EmbeddingRequest { Model = _embeddingModel, Inputs = [text] }, cancellationToken)
            .ConfigureAwait(false);

        return embedded.Vectors[0];
    }

    /// <summary>Asks one reader one text.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="text">The text to search with.</param>
    /// <param name="embedding">Its query vector.</param>
    /// <param name="topK">How many chunks may come back.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What it answered.</returns>
    private static ValueTask<RetrievalResult> AskAsync(
        IRetrievalBackend reader,
        string text,
        ReadOnlyMemory<float> embedding,
        int topK,
        CancellationToken cancellationToken) =>
        reader.SearchAsync(
            new RetrievalQuery { Text = text, Embedding = embedding, TopK = Math.Max(topK, 1) },
            cancellationToken);

    /// <summary>
    /// Searches every collection the session may read, in the runbook's own words, and merges what they answer.
    /// </summary>
    /// <remarks>
    /// The original's two stages, in its order. Every permitted collection is <em>probed</em> with the question as asked -
    /// the original query's vector, not the widened one - and the strongest few get the deep search. Selection spends the
    /// deep search rather than narrowing the answer: a collection that lost the selection still contributes its probe
    /// pool to the merge, which is why the collections recorded as searched are all of them.
    /// <para>
    /// One difference from the original, and it is a real one: it fans the probe out under a concurrency setting and this
    /// port asks one collection at a time. The events still stream per collection as each answers, which is what the
    /// bounded fan-out was for, but a wide runbook pays the sum of the probes rather than their maximum.
    /// </para>
    /// </remarks>
    /// <param name="document">The runbook.</param>
    /// <param name="permitted">The collections the clearance permits, in the runbook's order.</param>
    /// <param name="question">The question as asked.</param>
    /// <param name="widened">The question as the expansion left it, which the deep search uses.</param>
    /// <param name="topK">The caller's override, or zero for the runbook's own.</param>
    /// <param name="onProgress">An optional listener.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The merged hits and one envelope per collection that answered.</returns>
    private async ValueTask<SessionTurnSearch> SearchCollectionsAsync(
        RunbookDocument document,
        IReadOnlyList<string> permitted,
        string question,
        string widened,
        int topK,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var indexes = _collections!;
        var selection = document.Spec.Retrieval?.CollectionSelection;
        var wanted = Math.Max(Wanted(document, topK), 1);
        var selected = await SelectAsync(document, permitted, question, onProgress, cancellationToken)
            .ConfigureAwait(false);

        // The envelopes the answer's chunks are explained by, one per collection that contributes to it: the pools that
        // lost the selection carry theirs already, and the deep search adds one as each chosen collection answers. That
        // is why the probe's envelopes are kept and not just its chunks - a chunk whose provenance is not recorded is a
        // chunk nobody can explain later.
        var contributions = selected.Envelopes;

        // The widened question is usually the question itself, and then the probe's own vector is the deep search's: one
        // embedding, exactly as the probe shares one across every collection it probes.
        var deepVector = selected.ProbeVector is { } original && string.Equals(widened, question, StringComparison.Ordinal)
            ? original
            : await EmbedAsync(widened, cancellationToken).ConfigureAwait(false);

        var deepK = selection is null ? wanted : Math.Max(wanted, selection.CandidatePoolPerCollection);
        var deep = new List<IReadOnlyList<RetrievedChunk>>();

        foreach (var collection in selected.Chosen)
        {
            var reader = await indexes.ReaderForAsync(collection, cancellationToken).ConfigureAwait(false);

            if (reader is null)
            {
                // A cutover between the probe and the deep search is the same fact the probe reports: this collection has
                // no index here to search.
                onProgress?.Invoke(new TurnRetrieved(collection, 0, Skipped: true));

                continue;
            }

            var answer = await AskAsync(reader, widened, deepVector, deepK, cancellationToken).ConfigureAwait(false);

            onProgress?.Invoke(new TurnRetrieved(collection, answer.Chunks.Count, Skipped: false));
            deep.Add(answer.Chunks);
            contributions[collection] = answer.Envelope;
        }

        // In the runbook's own order, which is the order a reader of the record expects to find the corpora named in.
        var envelopes = permitted
            .Where(contributions.ContainsKey)
            .Select(collection => contributions[collection])
            .ToArray();

        return new SessionTurnSearch(ReciprocalRankFusion.Fuse([.. deep, .. selected.Unselected], wanted), envelopes);
    }

    /// <summary>What the probe decided, before the deep search runs.</summary>
    /// <param name="ProbeVector">The question's vector, or <see langword="null"/> when no selection was declared.</param>
    /// <param name="Chosen">The collections the deep search gets, in the runbook's order.</param>
    /// <param name="Unselected">The probe pools of the collections that lost the selection.</param>
    /// <param name="Envelopes">The envelopes of the collections that lost the selection.</param>
    private sealed record Selection(
        ReadOnlyMemory<float>? ProbeVector,
        List<string> Chosen,
        List<IReadOnlyList<RetrievedChunk>> Unselected,
        Dictionary<string, ProvenanceEnvelope> Envelopes);

    /// <summary>
    /// Probes every collection the session may read, and selects the strongest few for the deep search.
    /// </summary>
    /// <remarks>
    /// The original's two stages, in its order. Every permitted collection is <em>probed</em> with the question as asked -
    /// the original query's vector, not the widened one - and the strongest few get the deep search. Selection spends the
    /// deep search rather than narrowing the answer: a collection that lost the selection still contributes its probe pool
    /// to the merge, which is why the collections recorded as searched are all of them.
    /// <para>
    /// One difference from the original, and it is a real one: it fans the probe out under a concurrency setting and this
    /// port asks one collection at a time. The events still stream per collection as each answers, which is what the
    /// bounded fan-out was for, but a wide runbook pays the sum of the probes rather than their maximum.
    /// </para>
    /// </remarks>
    /// <param name="document">The runbook.</param>
    /// <param name="permitted">The collections the clearance permits, in the runbook's order.</param>
    /// <param name="question">The question as asked.</param>
    /// <param name="onProgress">An optional listener.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the probe decided.</returns>
    private async ValueTask<Selection> SelectAsync(
        RunbookDocument document,
        IReadOnlyList<string> permitted,
        string question,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var indexes = _collections!;
        var selection = document.Spec.Retrieval?.CollectionSelection;
        var pools = new List<CollectionPool>();
        var probed = new Dictionary<string, ProvenanceEnvelope>(StringComparer.Ordinal);

        if (selection is null)
        {
            // Without a declared selection there is no probe: every permitted collection gets the deep search, and the
            // events that follow are the retrieval ones.
            return new Selection(null, [.. permitted], [], []);
        }

        var probeK = (int)Math.Clamp(selection.ProbeCandidateN, 1, int.MaxValue);
        var probeVector = await EmbedAsync(question, cancellationToken).ConfigureAwait(false);

        foreach (var collection in permitted)
        {
            var reader = await indexes.ReaderForAsync(collection, cancellationToken).ConfigureAwait(false);

            if (reader is null)
            {
                onProgress?.Invoke(new TurnProbed(collection, 0, Skipped: true));

                continue;
            }

            var probe = await AskAsync(reader, question, probeVector, probeK, cancellationToken).ConfigureAwait(false);

            onProgress?.Invoke(new TurnProbed(collection, probe.Chunks.Count, Skipped: false));
            pools.Add(new CollectionPool(collection, probe.Chunks));
            probed[collection] = probe.Envelope;
        }

        var ranked = CollectionSelection.Rank(pools, question, selection.PhraseBoost);
        var strongest = ranked
            .Take(Math.Max(0, selection.MaxCollections))
            .Select(index => pools[index].Collection)
            .ToHashSet(StringComparer.Ordinal);

        var chosen = permitted.Where(strongest.Contains).ToList();
        var unselected = new List<IReadOnlyList<RetrievedChunk>>();
        var envelopes = new Dictionary<string, ProvenanceEnvelope>(StringComparer.Ordinal);

        // An all-empty probe is not evidence that no collection can answer, so the selection falls back to every
        // permitted collection - and the probe pools are dropped with it, because nothing is merged twice.
        if (chosen.Count == 0)
        {
            onProgress?.Invoke(new TurnSelected(pools.Count, permitted.Count, [.. permitted]));

            return new Selection(probeVector, [.. permitted], [], envelopes);
        }

        foreach (var pool in pools.Where(pool => !strongest.Contains(pool.Collection)))
        {
            unselected.Add(pool.Hits);
            envelopes[pool.Collection] = probed[pool.Collection];
        }

        onProgress?.Invoke(new TurnSelected(pools.Count, chosen.Count, chosen));

        return new Selection(probeVector, chosen, unselected, envelopes);
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
    /// <param name="chunks">The merged hits.</param>
    /// <param name="models">The models each paid step uses.</param>
    /// <param name="onProgress">An optional listener; the turn's result never depends on one being present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer and what it cost.</returns>
    private async ValueTask<TurnOutcome> AnswerOverHitsAsync(
        TurnRequest request,
        IReadOnlyList<RetrievedChunk> chunks,
        TurnModels models,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken) =>
        await TurnPipeline
            .AnswerOverTextAsync(
                request,
                RenderHits(chunks),
                [.. chunks.Select(chunk => chunk.Text)],
                Labels(chunks),
                new AnsweringModel(_model, models.Completion),
                onProgress,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The labels an answer may cite out of a retrieval.</summary>
    /// <param name="chunks">The merged hits.</param>
    /// <returns>The citable labels.</returns>
    private static IReadOnlyList<string> Labels(IReadOnlyList<RetrievedChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        return
        [
            .. chunks.Select(chunk => chunk.Source.ChunkId),
            .. chunks.Select(chunk => chunk.Source.SourcePath),
        ];
    }

    /// <summary>Renders the retrieved chunks as the labelled blocks a prompt carries.</summary>
    /// <param name="chunks">The merged hits.</param>
    /// <returns>The rendered context.</returns>
    private static string RenderHits(IReadOnlyList<RetrievedChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        return string.Join("\n\n", chunks.Select(chunk => $"[{chunk.Source.ChunkId}] {chunk.Text}"));
    }
}
