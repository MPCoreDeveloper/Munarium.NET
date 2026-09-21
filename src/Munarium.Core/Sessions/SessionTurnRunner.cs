namespace Munarium.Sessions;

using Munarium.Budgets;
using Munarium.Evidence;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Runbooks;

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
/// <param name="embedder">The model that embeds the question.</param>
/// <param name="model">The model that classifies and answers.</param>
/// <param name="embeddingModel">The embedding model to ask for.</param>
/// <param name="providers">The evidence providers, in trust order.</param>
/// <param name="tenant">The deployment's tenant.</param>
/// <param name="collections">
/// The collections this deployment can search one at a time, or <see langword="null"/> when it built one corpus.
/// </param>
/// <param name="ceiling">
/// The deployment's paid-call ceilings, or <see langword="null"/> for the built-ins. The turn reads its completion, its
/// expansion and its classifier ceilings here, because a runbook that declares none has to be held to what the
/// deployment says rather than to a constant compiled into the kernel.
/// </param>
public sealed class SessionTurnRunner(
    ISessionStore sessions,
    IIndexHost index,
    IModelProvider embedder,
    IModelProvider model,
    string embeddingModel,
    IReadOnlyList<IEvidenceProvider> providers,
    string tenant,
    ICollectionIndexes? collections = null,
    MaxTokensCeiling? ceiling = null)
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
    /// Gets the collections this deployment can search one at a time, or <see langword="null"/> when it built one corpus.
    /// </summary>
    /// <remarks>
    /// Optional on purpose: a deployment with a single serving index is a deployment with one collection as far as a turn
    /// is concerned, and every turn before this seam existed worked that way. When it is present, a turn searches each
    /// permitted collection's own live version; when it is not, it searches the one that serves.
    /// </remarks>
    private readonly ICollectionIndexes? _collections = collections;
    private readonly MaxTokensCeiling? _ceiling = ceiling;

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

        var budgets = await CeilingAsync(cancellationToken).ConfigureAwait(false);

        var intent = await IntentResolution
            .ResolveAsync(
                document,
                question,
                _model,
                models.Intent,
                maxTokens: budgets.HierarchyClassifier,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var (profile, problem) = ResearchProfiles.Resolve(
            document.Spec.Retrieval?.ResearchProfiles ?? [],
            requestedProfile,
            document.Spec.Retrieval?.DefaultResearchProfile);

        return problem is not null
            ? problem
            : await AnswerAsync(
                    session,
                    document,
                    question,
                    intent,
                    profile,
                    models,
                    budgets,
                    complete,
                    topK,
                    onProgress,
                    cancellationToken)
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
        MaxTokensBudget budgets,
        bool complete,
        int topK,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        // The clearance is the session's rather than the caller's, which is the whole point of the snapshot: a turn
        // reads what the conversation was opened to read.
        var searched = SessionCreation.PermittedCollections(document, session.Access);
        var plan = profile is null ? null : ResearchProfiles.BuildPlan(profile, intent);
        var request = Completion(document, complete, question, budgets.TurnCompletion);
        SessionTurnSearch? hits = null;

        // Which model is about to answer is known before anything is paid for, which is the only moment a stream can
        // say it. A turn that runs no completion resolves none, so it reports none.
        if (request is not null)
        {
            onProgress?.Invoke(new TurnModelResolved(_model.Id.Value, models.Completion, Tier: null, WasOverride: false));
        }

        // One search per turn, however many layers ask for it: the evidence hierarchy composes what the retrieval
        // returned, it does not retrieve once per layer.
        async ValueTask<SessionTurnSearch> SearchOnceAsync(CancellationToken token)
        {
            if (hits is { } already) return already;

            var widened = await WidenAsync(document, question, models.Expansion, budgets.QueryExpansion, onProgress, token)
                .ConfigureAwait(false);

            // The fan-out when the deployment has an index per collection, and the one serving index otherwise - which
            // is what a deployment that built a single corpus has, and what every turn did before collections could be
            // searched one at a time.
            hits = _collections is null || !await HasOwnIndexesAsync(searched, token).ConfigureAwait(false)
                ? SessionTurnSearch.Of(await SearchAsync(document, widened, topK, token).ConfigureAwait(false))
                : await SearchCollectionsAsync(document, searched, question, widened, topK, onProgress, token)
                    .ConfigureAwait(false);

            // Reported where the retrieval returns rather than where the turn reads it: that is the boundary, and a
            // layer that asks second is served the same search rather than a second one.
            onProgress?.Invoke(new TurnMerged(hits.Chunks.Count));

            return hits;
        }

        // What a document layer takes: one retrieval, whose chunks are the merged hits. Its envelope is a shape rather
        // than the turn's provenance, which is recorded per collection - see SessionTurnSearch.AsOne.
        async ValueTask<RetrievalResult> DocumentsAsync(CancellationToken token) =>
            (await SearchOnceAsync(token).ConfigureAwait(false)).AsOne();

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
                        (_, token) => DocumentsAsync(token),
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
                        (_, token) => DocumentsAsync(token),
                        served => Labels(served.Chunks),
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
            outcome = await AnswerOverHitsAsync(request, retrieved.Chunks, models, onProgress, cancellationToken)
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
            Intent = intent,
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
        var pools = new List<CollectionPool>();
        List<string> chosen;
        var unselected = new List<IReadOnlyList<RetrievedChunk>>();

        // One envelope per collection whose chunks reach the answer, which is what an envelope is for: a chunk whose
        // provenance is not recorded is a chunk nobody can explain later. A collection that was probed and lost the
        // selection contributes its pool to the merge, so its envelope belongs in the record too - which is why the
        // probe's envelopes are kept and not just its chunks.
        var contributions = new Dictionary<string, ProvenanceEnvelope>(StringComparer.Ordinal);
        var probed = new Dictionary<string, ProvenanceEnvelope>(StringComparer.Ordinal);

        var probeVector = selection is null
            ? (ReadOnlyMemory<float>?)null
            : await EmbedAsync(question, cancellationToken).ConfigureAwait(false);

        if (selection is null)
        {
            // Without a declared selection there is no probe: every permitted collection gets the deep search, and the
            // events that follow are the retrieval ones.
            chosen = [.. permitted];
        }
        else
        {
            var probeK = (int)Math.Clamp(selection.ProbeCandidateN, 1, int.MaxValue);

            foreach (var collection in permitted)
            {
                var reader = await indexes.ReaderForAsync(collection, cancellationToken).ConfigureAwait(false);

                if (reader is null)
                {
                    onProgress?.Invoke(new TurnProbed(collection, 0, Skipped: true));

                    continue;
                }

                var probe = await AskAsync(reader, question, probeVector!.Value, probeK, cancellationToken)
                    .ConfigureAwait(false);

                onProgress?.Invoke(new TurnProbed(collection, probe.Chunks.Count, Skipped: false));
                pools.Add(new CollectionPool(collection, probe.Chunks));
                probed[collection] = probe.Envelope;
            }

            var ranked = CollectionSelection.Rank(pools, question, selection.PhraseBoost);
            var strongest = ranked
                .Take(Math.Max(0, selection.MaxCollections))
                .Select(index => pools[index].Collection)
                .ToHashSet(StringComparer.Ordinal);

            chosen = [.. permitted.Where(strongest.Contains)];

            // An all-empty probe is not evidence that no collection can answer, so the selection falls back to every
            // permitted collection - and the probe pools are dropped with it, because nothing is merged twice.
            if (chosen.Count == 0)
            {
                chosen = [.. permitted];
            }
            else
            {
                foreach (var pool in pools.Where(pool => !strongest.Contains(pool.Collection)))
                {
                    unselected.Add(pool.Hits);
                    contributions[pool.Collection] = probed[pool.Collection];
                }
            }

            onProgress?.Invoke(new TurnSelected(pools.Count, chosen.Count, chosen));
        }

        // The widened question is usually the question itself, and then the probe's own vector is the deep search's: one
        // embedding, exactly as the probe shares one across every collection it probes.
        var deepVector = probeVector is { } original && string.Equals(widened, question, StringComparison.Ordinal)
            ? original
            : await EmbedAsync(widened, cancellationToken).ConfigureAwait(false);

        var deepK = selection is null ? wanted : Math.Max(wanted, selection.CandidatePoolPerCollection);
        var deep = new List<IReadOnlyList<RetrievedChunk>>();

        foreach (var collection in chosen)
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

        return new SessionTurnSearch(ReciprocalRankFusion.Fuse([.. deep, .. unselected], wanted), envelopes);
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
                _model,
                models.Completion,
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
