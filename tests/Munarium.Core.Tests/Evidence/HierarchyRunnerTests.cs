namespace Munarium.Core.Tests.Evidence;

using Munarium.Evidence;
using Munarium.Ledger;
using Munarium.Retrieval;
using static Munarium.Core.Tests.Support.HierarchyFixture;


/// <summary>
/// Tests for the hierarchy runner: which layers run, what a source nobody claims does, and what stops a turn.
/// </summary>
public class HierarchyRunnerTests
{
    /// <summary>
    /// A fallback layer is a last resort rather than another opinion: it runs only when nothing before it produced
    /// evidence, so a weak or expensive source is never consulted as a matter of course.
    /// </summary>
    [Fact]
    public async Task AFallbackLayerRunsOnlyWhenNothingBeforeItProducedEvidence()
    {
        var answered = new DocumentPath("doc-1");
        var withEvidence = Produced(await HierarchyRunner.ExecuteAsync(
            Plan(
                Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary),
                Layer("archive", "archive", LayerRequirement.Fallback, AnswerRole.Supporting)),
            [],
            answered.RunAsync));

        Assert.Equal(["contracts"], withEvidence.Decision.Layers.Select(outcome => outcome.Layer));
        Assert.Equal(["contracts"], answered.LayerNames);

        var silent = new DocumentPath();
        var fellBack = Produced(await HierarchyRunner.ExecuteAsync(
            Plan(
                Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary),
                Layer("archive", "archive", LayerRequirement.Fallback, AnswerRole.Supporting)),
            [],
            silent.RunAsync));

        Assert.Equal(["contracts", "archive"], fellBack.Decision.Layers.Select(outcome => outcome.Layer));
        Assert.Equal(["contracts", "archive"], silent.LayerNames);
    }

    /// <summary>
    /// The defect this rule exists to prevent: with nothing bound to it, a layer reading a plane must refuse
    /// rather than quietly become a document search and report its required layer satisfied.
    /// </summary>
    [Fact]
    public async Task APlaneQualifiedSourceNobodyClaimsRefusesInsteadOfSearchingDocuments()
    {
        var documents = new DocumentPath("doc-1");
        var events = new List<HierarchyProgress>();

        var refusal = Refused(await HierarchyRunner.ExecuteAsync(
            Plan(Layer("register", "matrix:register", LayerRequirement.Required, AnswerRole.Primary)),
            [],
            documents.RunAsync,
            events.Add));

        Assert.Equal("register", refusal.Layer);
        Assert.Equal(EvidenceRefusalCodes.SourceNotBound, refusal.RefusalCode);
        Assert.Equal(0, documents.Calls);
        Assert.Contains(events, item => item is LayerCompleted completed && completed.RefusalCode is EvidenceRefusalCodes.SourceNotBound);
    }

    /// <summary>
    /// Retrieval returns the top-k it found, never a proof that nothing else exists, so a document layer can
    /// answer a turn but can never let it claim completeness.
    /// </summary>
    [Fact]
    public async Task ABareSourceNameIsServedByTheDocumentPathAndNeverSupportsCompleteness()
    {
        var documents = new DocumentPath("doc-1", "doc-2");
        var answered = Produced(await HierarchyRunner.ExecuteAsync(
            Plan(Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary)),
            [],
            documents.RunAsync));

        Assert.Equal("document_hits", Assert.Single(answered.Blocks).Block.KindName());
        Assert.False(answered.Decision.CompletenessAvailable);
        Assert.False(Assert.Single(answered.Decision.Layers).SupportsCompleteness);
        Assert.NotNull(answered.Documents);
        Assert.Equal(
            ["doc-1", "doc-2"],
            answered.Documents.Chunks.Select(chunk => chunk.Source.ChunkId));
    }

    [Fact]
    public async Task ARequiredLayerThatRefusedStopsTheTurn()
    {
        var refused = Refused(await HierarchyRunner.ExecuteAsync(
            Plan(Layer("contracts", "matrix:contracts", LayerRequirement.Required, AnswerRole.Primary)),
            [new StubProvider("matrix", "matrix:", Unavailable())],
            new DocumentPath("doc-1").RunAsync));

        Assert.Equal("contracts", refused.Layer);
        Assert.Equal(EvidenceRefusalCodes.SourceUnavailable, refused.RefusalCode);
    }

    [Fact]
    public async Task AnOptionalLayerThatRefusedDoesNotStopTheTurn()
    {
        var answered = Produced(await HierarchyRunner.ExecuteAsync(
            Plan(
                Layer("contracts", "matrix:contracts", LayerRequirement.Optional, AnswerRole.Supporting),
                Layer("notes", "notes", LayerRequirement.Required, AnswerRole.Primary)),
            [new StubProvider("matrix", "matrix:", Unavailable())],
            new DocumentPath("doc-1").RunAsync));

        Assert.Equal(["contracts", "notes"], answered.Decision.Layers.Select(outcome => outcome.Layer));
        Assert.Equal(EvidenceRefusalCodes.SourceUnavailable, answered.Decision.Layers[0].RefusalCode);
        Assert.Null(answered.Decision.RequiredLayerFailed());
    }

    /// <summary>
    /// A conflict between layers is disclosed, never resolved in favour of the higher one: both counts stand, and
    /// the count of disagreements is part of the record.
    /// </summary>
    [Fact]
    public async Task ConflictingCountsAreDisclosedRatherThanResolved()
    {
        var answered = Produced(await HierarchyRunner.ExecuteAsync(
            Plan(
                Layer("invoices", "matrix:invoices", LayerRequirement.Optional, AnswerRole.Primary),
                Layer("payments", "matrix:payments", LayerRequirement.Optional, AnswerRole.Primary)),
            [
                new StubProvider("invoices", "matrix:invoices", new CountBlock { Value = 12, RowsCovered = 12 }),
                new StubProvider("payments", "matrix:payments", new CountBlock { Value = 15, RowsCovered = 15 }),
            ],
            new DocumentPath().RunAsync));

        Assert.Equal(1, answered.Decision.DisclosedConflicts);
        Assert.Equal(EvidencePlan.PreserveAndDisclose, answered.Decision.ConflictsPolicy);
        Assert.True(answered.Decision.CompletenessAvailable);
        Assert.Equal(2, answered.Blocks.Count);
    }

    [Fact]
    public async Task ATruncatedTableCannotBackACompletenessClaim()
    {
        var truncated = Produced(await HierarchyRunner.ExecuteAsync(
            Plan(Layer("billing", "matrix:billing", LayerRequirement.Required, AnswerRole.Primary)),
            [new StubProvider("billing", "matrix:billing", Table(truncated: true))],
            new DocumentPath().RunAsync));

        Assert.False(truncated.Decision.CompletenessAvailable);

        var whole = Produced(await HierarchyRunner.ExecuteAsync(
            Plan(Layer("billing", "matrix:billing", LayerRequirement.Required, AnswerRole.Primary)),
            [new StubProvider("billing", "matrix:billing", Table(truncated: false))],
            new DocumentPath().RunAsync));

        Assert.True(whole.Decision.CompletenessAvailable);
        Assert.Equal("ev-1", Assert.Single(whole.Decision.Layers).EvidenceId);
    }

    [Fact]
    public async Task ProvidersAreTriedInOrderAndTheFirstThatClaimsTheSourceWins()
    {
        var first = new StubProvider("first", "matrix:billing", new CountBlock { Value = 1 });
        var second = new StubProvider("second", "matrix:", new CountBlock { Value = 2 });

        var answered = Produced(await HierarchyRunner.ExecuteAsync(
            Plan(Layer("billing", "matrix:billing", LayerRequirement.Required, AnswerRole.Primary)),
            [first, second],
            new DocumentPath().RunAsync));

        Assert.Equal(1, Assert.Single(answered.Blocks).Block switch { CountBlock count => count.Value, _ => 0 });
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    /// <summary>
    /// A provider that calls itself <c>documents</c> is the document path: claiming the bare name means serving it
    /// with real retrieval rather than with a block of its own.
    /// </summary>
    [Fact]
    public async Task AProviderThatCallsItselfDocumentsIsServedByTheDocumentPath()
    {
        var documents = new DocumentPath("doc-1");
        var claiming = new StubProvider(EvidencePlanes.Documents, "contracts", new CountBlock { Value = 1 });

        var answered = Produced(await HierarchyRunner.ExecuteAsync(
            Plan(Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary)),
            [claiming],
            documents.RunAsync));

        Assert.Equal(["contracts"], documents.LayerNames);
        Assert.Equal(0, claiming.Calls);
        Assert.Equal("document_hits", Assert.Single(answered.Blocks).Block.KindName());
    }

    /// <summary>
    /// The order is the story a caller reads while a turn runs: the profile, then each layer's start, the source
    /// that served it, what it produced, and finally what the run's blocks collectively permit.
    /// </summary>
    [Fact]
    public async Task ProgressReportsTheRunInOrder()
    {
        var events = new List<HierarchyProgress>();

        Produced(await HierarchyRunner.ExecuteAsync(
            Plan(Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary, preserve: true)),
            [],
            new DocumentPath("doc-1").RunAsync,
            events.Add));

        var kinds = new List<string>();
        ProfileResolved? profile = null;
        foreach (var item in events)
        {
            if (item is ProfileResolved resolved)
            {
                profile = resolved;
            }

            kinds.Add(item switch
            {
                ProfileResolved => "profile",
                LayerStarted => "layer-started",
                SourceBound => "source-bound",
                LayerCompleted => "layer-completed",
                _ => "coverage",
            });
        }

        Assert.Equal(["profile", "layer-started", "source-bound", "layer-completed", "coverage"], kinds);
        Assert.Equal("due-diligence", profile?.Profile);
        Assert.Equal(["contracts"], profile?.Layers);
    }

    private static HierarchyOutcome Produced(HierarchyResult result) =>
        result is HierarchyOutcome outcome
            ? outcome
            : throw new InvalidOperationException("The run refused.");

    private static RequiredLayerUnavailable Refused(HierarchyResult result) =>
        result is RequiredLayerUnavailable refusal
            ? refusal
            : throw new InvalidOperationException("The run answered.");

    private sealed class StubProvider(string id, string serves, EvidenceBlock block) : IEvidenceProvider
    {
        public int Calls { get; private set; }

        public string Id => id;

        public bool CanServe(string source) => source.StartsWith(serves, StringComparison.Ordinal);

        public ValueTask<EvidenceBlock> FetchAsync(
            EvidenceLayer layer,
            QueryIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;

            return ValueTask.FromResult(block);
        }
    }

    private sealed class DocumentPath(params string[] chunkIds)
    {
        public int Calls { get; private set; }

        public List<string> LayerNames { get; } = [];

        private static RetrievedChunk Chunk(string chunkId) => new(
            new SourceReference(chunkId, "source-1", "docs/policy.pdf", "sha256:abc", ChunkOrdinal: 0),
            Score: 0,
            $"text of {chunkId}");

        public ValueTask<RetrievalResult> RunAsync(EvidenceLayer layer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LayerNames.Add(layer.Name);

            var chunks = chunkIds.Select(Chunk).ToList();

            return ValueTask.FromResult(new RetrievalResult(
                chunks,
                new ProvenanceEnvelope("index@1", SequenceNumber.Zero, [.. chunks.Select(chunk => chunk.Source)])));
        }
    }
}
