namespace Munarium.Core.Tests.Sessions;

using Munarium.Access;
using Munarium.Evidence;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Runbooks;
using Munarium.Sessions;

/// <summary>
/// Tests for the turn runner: what it searches, what it records, and what stops it before a model is paid for.
/// </summary>
public class SessionTurnRunnerTests
{
    private static SessionTurnExecuted Executed(SessionTurnResult result) =>
        result is SessionTurnExecuted executed
            ? executed
            : throw new InvalidOperationException("The turn produced nothing.");

    private static RequiredLayerUnavailable Refused(SessionTurnResult result) =>
        result is RequiredLayerUnavailable refusal
            ? refusal
            : throw new InvalidOperationException("The turn produced something.");

    private static SessionRefusal Declined(SessionTurnResult result) =>
        result is SessionRefusal refusal
            ? refusal
            : throw new InvalidOperationException("The turn was not declined.");

    private static SessionRecord Session() => new()
    {
        Tenant = "acme",
        Id = SessionIds.New(),
        Uid = "ada",
        RunbookRef = "northgate@3",
        Access = new AccessContext(2, []),
        State = SessionState.Open,
    };

    private static RunbookDocument Document(bool withProfile = false, ModelQueryExpansionSpec? expansion = null) => new()
    {
        ApiVersion = "munarium.dev/v2",
        Kind = "Runbook",
        Metadata = new RunbookMeta { Name = "northgate", Version = 3 },
        Spec = new RunbookSpec
        {
            Collections =
            [
                new CollectionSpec { Name = "contracts", Shape = "cuad-contracts@3", AccessLevel = 2 },
                new CollectionSpec { Name = "minutes", Shape = "minutes@1", AccessLevel = 4 },
            ],

            // The step runs only when the runbook both declares it and pins the task that widens a query, which is the
            // original's rule: a declaration without a task level is a profile asking for something no model was named
            // for.
            Models = expansion is null
                ? new ModelsSpec()
                : new ModelsSpec
                {
                    Tasks = new SortedDictionary<string, ModelSpec>(StringComparer.Ordinal)
                    {
                        [TaskLevels.QueryExpansion] = new ModelSpec(),
                    },
                },
            Retrieval = new RetrievalSpec
            {
                TopK = 5,
                ModelQueryExpansion = expansion,
                DefaultResearchProfile = withProfile ? "register-first" : null,
                ResearchProfiles = withProfile
                    ?
                    [
                        new ResearchProfile
                        {
                            Name = "register-first",
                            Layers =
                            [
                                new ResearchLayer
                                {
                                    Name = "register",
                                    Sources = ["matrix:register"],
                                    Requirement = LayerRequirement.Required,
                                    Role = AnswerRole.Controlling,
                                },
                            ],
                        },
                    ]
                    : [],
            },
            Completion = new CompletionSpec
            {
                PromptTemplate = "Context:\n{context}\n\nQ: {query}",
                Verification = new VerificationSpec { Quotes = true, Citations = true, MaxRetries = 1 },
            },
        },
    };

    private static SessionTurnRunner Runner(ISessionStore sessions, IModelProvider model) =>
        Runner(sessions, model, new StubIndexHost());

    private static SessionTurnRunner Runner(ISessionStore sessions, IModelProvider model, StubIndexHost index) =>
        new(sessions, index, model, model, "test-embedder", [], "acme");

    /// <summary>
    /// A turn reports the stages it crosses, in the order it crosses them - and reports nothing it did not do.
    /// </summary>
    /// <remarks>
    /// The order is asserted because the order is the claim each event makes: the model is resolved before anything is
    /// paid for, the retrieval is reported once per turn however many layers ask for it, and a check follows each
    /// answer. Absence is asserted for the same reason a decision is kept off a turn that ran outside a profile: a
    /// listener cannot tell "no layer ran" from "a layer ran and reported nothing" unless the events simply do not
    /// appear.
    /// </remarks>
    [Fact]
    public async Task ATurnReportsTheStagesItCrosses()
    {
        var sessions = new MemorySessions();
        var session = await sessions.CreateAsync(Session());
        var model = new StubModel("The policy holds. \"text of doc-1\" [doc-1]");
        var reported = new List<TurnProgress>();

        _ = Executed(await Runner(sessions, model).RunAsync(
            session,
            Document(),
            "how many contracts lapse?",
            requestedProfile: null,
            new TurnModels("small-model", "small-model", "big-model"),
            complete: true,
            topK: 0,
            onProgress: reported.Add));

        var stages = reported.Select(StageOf).ToList();

        // The whole vocabulary of a turn that ran outside a profile, in the order it crosses it: the model is resolved
        // before anything is paid for, the retrieval is reported once (one search however many layers ask for it), and
        // the answer is read back by the check that follows it. No profile, no layer, and no compose - nothing composed
        // a hierarchy's blocks here, and saying so would be reporting a stage that never ran.
        Assert.Equal(["model", "merge", "completion", "verify"], stages);
    }

    /// <summary>
    /// A turn under a profile reports the profile and its layers, and stops reporting where the turn stopped.
    /// </summary>
    /// <remarks>
    /// The fixture's profile requires a plane no provider is bound to, which is the interesting case for a stream: the
    /// turn is refused before a model is paid for, so the events say so by having no completion and no retrieval in them
    /// rather than by reporting a stage that never ran.
    /// </remarks>
    [Fact]
    public async Task ATurnUnderAProfileReportsTheHierarchyAndThenStops()
    {
        var sessions = new MemorySessions();
        var session = await sessions.CreateAsync(Session());
        var model = new StubModel("The register is the source.");
        var reported = new List<TurnProgress>();

        _ = Refused(await Runner(sessions, model).RunAsync(
            session,
            Document(withProfile: true),
            "what does the register say?",
            requestedProfile: null,
            new TurnModels("small-model", "small-model", "big-model"),
            complete: true,
            topK: 0,
            onProgress: reported.Add));

        var stages = reported.Select(StageOf).ToList();

        // Every layer ran, so coverage is reported - a layer that could not answer completed with a refusal, which is
        // what its completion says, and the run concludes over the blocks it collected.
        Assert.Equal(["model", "profile", "layer_start", "layer_complete", "coverage"], stages);

        // And the turn stopped there: a required layer that cannot answer is refused before the retrieval runs, so
        // nothing downstream of it was reported and nothing was paid for.
        Assert.DoesNotContain("merge", stages);
        Assert.DoesNotContain("compose", stages);
        Assert.DoesNotContain("completion", stages);
        Assert.DoesNotContain("verify", stages);
    }

    /// <summary>
    /// A turn that declares the query-expansion step searches with what the model widened, and reports the paid step.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they are asserted together: the variants have to reach the text the search reads - a step
    /// that is paid for and then not used is worse than no step - and the caller has to be able to see which model ran,
    /// which terms were accepted and what the call cost.
    /// </remarks>
    [Fact]
    public async Task ATurnSearchesWithTheQueryAModelWidened()
    {
        var sessions = new MemorySessions();
        var session = await sessions.CreateAsync(Session());
        var model = new StubModel("[\"expire\", \"renew\", \"lapsed\"]");
        var index = new StubIndexHost();
        var reported = new List<TurnProgress>();
        var expanded = new List<TurnExpanded>();

        _ = Executed(await Runner(sessions, model, index).RunAsync(
            session,
            Document(expansion: new ModelQueryExpansionSpec { MaxTerms = 3 }),
            "how many contracts lapse?",
            requestedProfile: null,
            new TurnModels("small-model", "small-model", "big-model"),
            complete: false,
            topK: 0,
            onProgress: progress =>
            {
                reported.Add(progress);

                if (progress is TurnExpanded value)
                {
                    expanded.Add(value);
                }
            }));

        Assert.Equal("how many contracts lapse? expire renew lapsed", Assert.Single(index.Queries));

        var applied = Assert.Single(expanded);

        Assert.Equal(["expire", "renew", "lapsed"], applied.Terms);
        Assert.Equal(ProviderId.Local.Value, applied.Provider);
        Assert.Equal("small-model", applied.Model);
        Assert.Equal(10, applied.InputTokens);
        Assert.Equal(5, applied.OutputTokens);

        // The step is reported before the search it widened, which is the only order that tells a reader the widening
        // happened before the candidates were chosen.
        Assert.Equal(["expansion", "merge"], reported.Select(StageOf));
    }

    /// <summary>A step that fails where the runbook lets it fail leaves the turn searching the question as asked.</summary>
    /// <remarks>
    /// And it reports nothing, because nothing happened: a stream that showed an expansion the runbook tolerated the
    /// failure of would be describing a step that produced no terms rather than one that produced none.
    /// </remarks>
    [Fact]
    public async Task AnExpansionTheRunbookToleratesLeavesTheQuestionAsAsked()
    {
        var sessions = new MemorySessions();
        var session = await sessions.CreateAsync(Session());
        var index = new StubIndexHost();
        var reported = new List<TurnProgress>();

        _ = Executed(await Runner(sessions, new FailingModel(), index).RunAsync(
            session,
            Document(expansion: new ModelQueryExpansionSpec()),
            "how many contracts lapse?",
            requestedProfile: null,
            new TurnModels("small-model", "small-model", "big-model"),
            complete: false,
            topK: 0,
            onProgress: reported.Add));

        Assert.Equal("how many contracts lapse?", Assert.Single(index.Queries));
        Assert.DoesNotContain("expansion", reported.Select(StageOf));
    }

    /// <summary>A step the runbook requires fails the turn when it cannot run.</summary>
    /// <remarks>
    /// Which is the whole difference between the two settings: a caller who asked for a widened search and silently got
    /// the narrow one is reading a different search than the one they configured.
    /// </remarks>
    [Fact]
    public async Task AnExpansionTheRunbookRequiresFailsTheTurn()
    {
        var sessions = new MemorySessions();
        var session = await sessions.CreateAsync(Session());
        var index = new StubIndexHost();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Runner(sessions, new FailingModel(), index).RunAsync(
                session,
                Document(expansion: new ModelQueryExpansionSpec { Required = true }),
                "how many contracts lapse?",
                requestedProfile: null,
                new TurnModels("small-model", "small-model", "big-model"),
                complete: false,
                topK: 0));

        Assert.Empty(index.Queries);
    }

    /// <summary>A model that embeds anything and cannot answer at all.</summary>
    private sealed class FailingModel : IModelProvider
    {
        public ProviderId Id => ProviderId.Local;

        public ValueTask<CompletionResponse> CompleteAsync(
            CompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            throw new HttpRequestException("the provider is not reachable");
        }

        public ValueTask<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            return ValueTask.FromResult(
                new EmbeddingResponse(
                    [.. request.Inputs.Select(_ => new ReadOnlyMemory<float>(new float[3]))],
                    request.Model,
                    new TokenUsage(1, 0)));
        }

        public ValueTask<ProviderHealth> HealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProviderHealth(true, "test"));
    }

    /// <summary>The wire's name for a progress event, which is the stage it reports.</summary>
    /// <param name="progress">The event.</param>
    /// <returns>The stage's name.</returns>
    private static string StageOf(TurnProgress progress) => progress switch
    {
        HierarchyProgress hierarchy => HierarchyStageOf(hierarchy),
        TurnModelResolved => "model",
        TurnExpanded => "expansion",
        TurnMerged => "merge",
        TurnComposed => "compose",
        TurnCompleted => "completion",
        TurnVerified => "verify",
    };

    /// <summary>The wire's name for one of the hierarchy's own events.</summary>
    /// <param name="progress">The event.</param>
    /// <returns>The stage's name.</returns>
    private static string HierarchyStageOf(HierarchyProgress progress) => progress switch
    {
        ProfileResolved => "profile",
        LayerStarted => "layer_start",
        SourceBound => "layer_source",
        LayerCompleted => "layer_complete",
        CoverageReported => "coverage",
    };

    /// <summary>A model that embeds anything and answers with one canned answer.</summary>
    private sealed class StubModel(string answer) : IModelProvider
    {
        public List<CompletionRequest> Completed { get; } = [];

        public ProviderId Id => ProviderId.Local;

        public ValueTask<CompletionResponse> CompleteAsync(
            CompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Completed.Add(request);

            return ValueTask.FromResult(new CompletionResponse(answer, request.Model, new TokenUsage(10, 5)));
        }

        public ValueTask<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                new EmbeddingResponse(
                    [.. request.Inputs.Select(_ => new ReadOnlyMemory<float>(new float[3]))],
                    request.Model,
                    new TokenUsage(1, 0)));
        }

        public ValueTask<ProviderHealth> HealthAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            throw new NotSupportedException("This double answers and embeds only.");
        }
    }

    /// <summary>An index host whose serving reader answers with one canned chunk.</summary>
    private sealed class StubIndexHost : IIndexHost
    {
        /// <summary>Gets the texts the turn searched with, in the order it searched.</summary>
        public List<string> Queries { get; } = [];

        public string Engine => "exact@1";

        public string ServingVersion => "idx-test";

        public IIndexWriter ServingWriter => throw new NotSupportedException("A turn writes nothing to an index.");

        public IRetrievalBackend ServingReader => new StubReader(Queries);

        public IRetrievalBackend? ReaderFor(string indexVersion) =>
            string.Equals(indexVersion, ServingVersion, StringComparison.Ordinal) ? new StubReader(Queries) : null;

        public IndexInstance Build(string indexVersion, SequenceNumber watermark) =>
            throw new NotSupportedException("A turn builds no index.");

        public bool Discard(string indexVersion) => false;

        public bool Serve(string indexVersion) => false;

        private sealed class StubReader(List<string> queries) : IRetrievalBackend
        {
            public ValueTask<RetrievalResult> SearchAsync(
                RetrievalQuery query,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(query);
                cancellationToken.ThrowIfCancellationRequested();

                queries.Add(query.Text);

                var chunk = new RetrievedChunk(
                    new SourceReference("doc-1", "source-1", "docs/policy.pdf", "sha256:abc", ChunkOrdinal: 0),
                    Score: 0,
                    "text of doc-1");

                return ValueTask.FromResult(
                    new RetrievalResult(
                        [chunk],
                        new ProvenanceEnvelope("idx-test", SequenceNumber.Zero, [chunk.Source])));
            }
        }
    }

    /// <summary>A session store held in memory: what the runner has to get right is the order of the turn, not the row.</summary>
    private sealed class MemorySessions : ISessionStore
    {
        public List<TurnRecord> Turns { get; } = [];

        public SessionRecord? Session { get; private set; }

        public ValueTask<SessionRecord> CreateAsync(SessionRecord session, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Session ??= session with { CreatedAt = "2026-09-18T00:00:00Z" };

            return ValueTask.FromResult(Session);
        }

        public ValueTask<SessionRecord?> GetAsync(
            string tenant,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(Session);
        }

        public ValueTask<int> AppendTurnAsync(TurnRecord turn, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Turns.Add(turn with { Ordinal = Turns.Count + 1, CreatedAt = "2026-09-18T00:00:01Z" });

            return ValueTask.FromResult(Turns.Count);
        }

        public ValueTask<bool> CloseAsync(
            string tenant,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Session is not { State: SessionState.Open } open)
            {
                return ValueTask.FromResult(false);
            }

            Session = open with { State = SessionState.Closed };

            return ValueTask.FromResult(true);
        }

        public ValueTask<IReadOnlyList<TurnRecord>> TurnsAsync(
            string tenant,
            string sessionId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<IReadOnlyList<TurnRecord>>([.. Turns.Take(limit)]);
        }

        public ValueTask<IReadOnlyList<SessionRecord>> RecentAsync(
            string tenant,
            string uid,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<IReadOnlyList<SessionRecord>>(Session is null ? [] : [Session]);
        }
    }

    /// <summary>
    /// A turn records the collections the clearance permitted, the hits and their provenance, and the answer with what
    /// the checks found - and the question reaches the model with the served text in the prompt.
    /// </summary>
    [Fact]
    public async Task ATurnRecordsWhatItSearchedAndWhatItAnswered()
    {
        var sessions = new MemorySessions();
        var session = await sessions.CreateAsync(Session());
        var model = new StubModel("The policy holds. \"text of doc-1\" [doc-1]");

        var executed = Executed(await Runner(sessions, model).RunAsync(
            session,
            Document(),
            "how many contracts lapse?",
            requestedProfile: null,
            new TurnModels("small-model", "small-model", "big-model"),
            complete: true,
            topK: 0));

        Assert.Equal(1, executed.Ordinal);
        Assert.Equal("how many contracts lapse?", executed.Question);

        // `contracts` is level 2 and permitted; `minutes` is level 4 and is not searched at all.
        Assert.Equal(["contracts"], executed.CollectionsSearched);
        Assert.Single(executed.Hits.Chunks);
        Assert.Null(executed.Decision);

        Assert.NotNull(executed.Completion);
        Assert.Empty(executed.Completion.Violations);
        Assert.Equal(1, executed.Completion.Completions);

        var recorded = Assert.Single(sessions.Turns);

        Assert.Equal(
            """[{"chunk_id":"doc-1","source_path":"docs/policy.pdf","score":0,"text":"text of doc-1"}]""",
            recorded.HitsJson);
        Assert.Contains("idx-test", recorded.EnvelopeJson, StringComparison.Ordinal);
        Assert.Null(recorded.HierarchyJson);
        Assert.Contains("\"completions\":1", recorded.CompletionJson, StringComparison.Ordinal);

        // The document path renders the hits as labelled blocks, and the prompt carries both the context and the task.
        var sent = Assert.Single(model.Completed);

        Assert.Contains("[doc-1] text of doc-1", sent.Prompt, StringComparison.Ordinal);
        Assert.Contains("Q: how many contracts lapse?", sent.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session that is no longer open accepts no turns, and nothing is recorded and nobody is asked.
    /// </summary>
    [Fact]
    public async Task AClosedSessionTakesNoTurns()
    {
        var sessions = new MemorySessions();
        var session = await sessions.CreateAsync(Session());
        Assert.True(await sessions.CloseAsync("acme", session.Id));

        // The turn is run against the session as stored rather than the copy that was opened: a caller holding a stale
        // open session does not get to keep asking.
        var closed = await sessions.GetAsync("acme", session.Id);
        Assert.NotNull(closed);

        var model = new StubModel("never asked");

        var refusal = Declined(await Runner(sessions, model).RunAsync(
            closed,
            Document(),
            "anything",
            requestedProfile: null,
            new TurnModels("small-model", "small-model", "big-model"),
            complete: true));

        Assert.Equal(SessionRefusalCodes.SessionClosed, refusal.Code);
        Assert.Contains("is closed", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(sessions.Turns);
        Assert.Empty(model.Completed);
    }

    /// <summary>
    /// A turn that asks for no answer still records its hits, because the caller who asked for evidence only may still
    /// ask why it got that evidence.
    /// </summary>
    [Fact]
    public async Task ATurnWithoutTheCompletionFlagStillRecordsWhatItFound()
    {
        var sessions = new MemorySessions();
        var session = await sessions.CreateAsync(Session());
        var model = new StubModel("never asked");

        var executed = Executed(await Runner(sessions, model).RunAsync(
            session,
            Document(),
            "how many contracts lapse?",
            requestedProfile: null,
            new TurnModels("small-model", "small-model", "big-model"),
            complete: false));

        Assert.Null(executed.Completion);
        Assert.Single(executed.Hits.Chunks);

        var recorded = Assert.Single(sessions.Turns);

        Assert.Null(recorded.CompletionJson);
        Assert.Contains("doc-1", recorded.HitsJson, StringComparison.Ordinal);
        Assert.Empty(model.Completed);
    }

    /// <summary>
    /// A required layer nobody can answer stops the turn before a model is paid for, and names the layer that could not
    /// be reached - which is the answer a caller can act on.
    /// </summary>
    [Fact]
    public async Task ARequiredLayerNobodyCanAnswerStopsTheTurnBeforeAnyCompletion()
    {
        var sessions = new MemorySessions();
        var session = await sessions.CreateAsync(Session());
        var model = new StubModel("never asked");

        var refusal = Refused(await Runner(sessions, model).RunAsync(
            session,
            Document(withProfile: true),
            "how many contracts lapse?",
            requestedProfile: null,
            new TurnModels("small-model", "small-model", "big-model"),
            complete: true));

        Assert.Equal("register", refusal.Layer);
        Assert.Equal(EvidenceRefusalCodes.SourceNotBound, refusal.RefusalCode);
        Assert.Empty(sessions.Turns);
        Assert.Empty(model.Completed);
    }
}

