namespace Munarium.Core.Tests.Evidence;

using Munarium.Core.Tests.Support;
using Munarium.Evidence;
using Munarium.Providers;
using Munarium.Retrieval;
using static Munarium.Core.Tests.Support.HierarchyFixture;

/// <summary>
/// Tests for the turn: what the model is asked, what it is charged for, and what happens to an answer the checks
/// refuse.
/// </summary>
public class TurnPipelineTests
{
    /// <summary>
    /// An answer that quotes what it was served and cites what it was served costs one completion - not two, not
    /// three: the checks exist to catch a lie, not to tax the truth.
    /// </summary>
    [Fact]
    public async Task AnAnswerGroundedInServedContentCostsOneCompletion()
    {
        var documents = new DocumentPath("doc-1");
        var model = new FakeModel(Answer("The policy holds. \"text of doc-1\" [doc-1]"));

        var outcome = Produced(await TurnPipeline.ExecuteAsync(
            Plan(Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary)),
            Request(),
            [],
            documents.RunAsync,
            Labels,
            model,
            "test-model"));

        Assert.Equal(1, outcome.Completions);
        Assert.Equal(0, outcome.Retries);
        Assert.Empty(outcome.Violations);
        Assert.Empty(outcome.FirstPassViolations);
        Assert.False(outcome.RetriedForTruncation);
        Assert.Equal(["quotes", "citations"], outcome.Checks);
        Assert.Contains("text of doc-1", outcome.Context, StringComparison.Ordinal);

        // The prompt is the template with the composed context and the question in it - both, because a model that
        // cannot see the question answers the context.
        var sent = Assert.Single(model.Requests);
        Assert.Contains("text of doc-1", sent.Prompt, StringComparison.Ordinal);
        Assert.Contains("how many contracts lapse this quarter?", sent.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fabricated quote and a citation to nothing the turn served are both violations, and the repair attempt is
    /// told which was which rather than merely that something failed.
    /// </summary>
    [Fact]
    public async Task AViolatingAnswerIsReAskedOnceWithItsViolationsAttached()
    {
        var documents = new DocumentPath("doc-1");
        var model = new FakeModel(
            Answer("It says \"the lighthouse was painted crimson that spring\" [docs/chunk-99]."),
            Answer("The log notes \"text of doc-1\" [doc-1]."));

        var outcome = Produced(await TurnPipeline.ExecuteAsync(
            Plan(Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary)),
            Request(),
            [],
            documents.RunAsync,
            Labels,
            model,
            "test-model"));

        Assert.Equal(
            [
                "quote: the lighthouse was painted crimson that spring",
                "citation: docs/chunk-99",
            ],
            outcome.FirstPassViolations);
        Assert.Empty(outcome.Violations);
        Assert.Equal(1, outcome.Retries);
        Assert.Equal(2, outcome.Completions);
        Assert.False(outcome.AnswerStandsWithViolations);

        var corrective = model.Requests[1].Prompt;
        Assert.Contains("the lighthouse was painted crimson that spring", corrective, StringComparison.Ordinal);
        Assert.Contains("[docs/chunk-99]", corrective, StringComparison.Ordinal);

        // A re-ask, not a targeted fetch: the second attempt has everything the first had.
        Assert.Contains("--- Original task, with the provided context ---", corrective, StringComparison.Ordinal);
        Assert.Contains("text of doc-1", corrective, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reasoning model spends hidden tokens from the same ceiling and can exhaust it before any visible text, so an
    /// early stop buys one re-ask at four times the ceiling - and the same prompt, because nothing was wrong with it.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatStopsEarlyIsAskedAgainAtFourTimesTheCeiling()
    {
        var documents = new DocumentPath("doc-1");
        var model = new FakeModel(
            Answer(string.Empty, stopReason: "max_tokens"),
            Answer("The log notes \"text of doc-1\" [doc-1]."));

        var outcome = Produced(await TurnPipeline.ExecuteAsync(
            Plan(Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary)),
            Request(),
            [],
            documents.RunAsync,
            Labels,
            model,
            "test-model"));

        Assert.True(outcome.RetriedForTruncation);
        Assert.Equal(2, outcome.Completions);
        Assert.Empty(outcome.Violations);
        Assert.Equal(64, model.Requests[0].MaxTokens);
        Assert.Equal(256, model.Requests[1].MaxTokens);
        Assert.Equal(model.Requests[0].Prompt, model.Requests[1].Prompt);

        // Every call is counted, not just the one that produced the answer that stands.
        Assert.Equal(20, outcome.InputTokens);
        Assert.Equal(10, outcome.OutputTokens);
    }

    /// <summary>
    /// A violation that survives the retries is recorded rather than hidden: the turn answers, says what it could not
    /// fix, and stops paying.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatStillViolatesStandsWithTheViolationRecorded()
    {
        var documents = new DocumentPath("doc-1");
        var model = new FakeModel(Answer("It says \"the lighthouse was painted crimson that spring\" [doc-1]."));

        var outcome = Produced(await TurnPipeline.ExecuteAsync(
            Plan(Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary)),
            Request(),
            [],
            documents.RunAsync,
            Labels,
            model,
            "test-model"));

        Assert.Equal(1, outcome.Retries);
        Assert.Equal(2, outcome.Completions);
        Assert.Equal(outcome.FirstPassViolations, outcome.Violations);
        Assert.True(outcome.AnswerStandsWithViolations);
        Assert.Equal(["quote: the lighthouse was painted crimson that spring"], outcome.Violations);
    }

    /// <summary>
    /// A runbook cannot buy more retries than the kernel allows, because the runbook is a document and the model calls
    /// are somebody's bill.
    /// </summary>
    [Fact]
    public async Task TheCorrectiveRetryCeilingIsClampedHoweverTheRunbookAsks()
    {
        var documents = new DocumentPath("doc-1");
        var model = new FakeModel(Answer("It says \"the lighthouse was painted crimson that spring\" [doc-1]."));

        var outcome = Produced(await TurnPipeline.ExecuteAsync(
            Plan(Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary)),
            Request(maxRetries: 9),
            [],
            documents.RunAsync,
            Labels,
            model,
            "test-model"));

        Assert.Equal(TurnPipeline.MaxCorrectiveRetries, outcome.Retries);
        Assert.Equal(TurnPipeline.MaxCorrectiveRetries + 1, outcome.Completions);
    }

    /// <summary>
    /// A corrective retry rides the ceiling the truncation retry raised, so a reasoning model gets the same headroom to
    /// repair its answer as it had to write one.
    /// </summary>
    [Fact]
    public async Task ACorrectiveRetryRidesTheCeilingTheTruncationRetryRaised()
    {
        var documents = new DocumentPath("doc-1");
        var model = new FakeModel(
            Answer(string.Empty, stopReason: "max_tokens"),
            Answer("It says \"the lighthouse was painted crimson that spring\" [doc-1]."),
            Answer("The log notes \"text of doc-1\" [doc-1]."));

        var outcome = Produced(await TurnPipeline.ExecuteAsync(
            Plan(Layer("contracts", "contracts", LayerRequirement.Required, AnswerRole.Primary)),
            Request(),
            [],
            documents.RunAsync,
            Labels,
            model,
            "test-model"));

        Assert.Equal(3, outcome.Completions);
        Assert.Equal(1, outcome.Retries);
        Assert.Empty(outcome.Violations);
        Assert.Equal(64, model.Requests[0].MaxTokens);
        Assert.Equal(256, model.Requests[2].MaxTokens);
    }

    /// <summary>
    /// A required layer nobody can answer stops the turn <em>before</em> a model is paid for. That is the whole point
    /// of a required layer: the alternative is a fluent answer about evidence that was never collected.
    /// </summary>
    [Fact]
    public async Task ARequiredLayerNobodyCanAnswerStopsTheTurnBeforeAModelIsPaidFor()
    {
        var documents = new DocumentPath("doc-1");
        var model = new FakeModel(Answer("never reached"));

        var refusal = Refused(await TurnPipeline.ExecuteAsync(
            Plan(Layer("register", "matrix:register", LayerRequirement.Required, AnswerRole.Primary)),
            Request(),
            [],
            documents.RunAsync,
            Labels,
            model,
            "test-model"));

        Assert.Equal("register", refusal.Layer);
        Assert.Equal(EvidenceRefusalCodes.SourceNotBound, refusal.RefusalCode);
        Assert.Empty(model.Requests);
        Assert.Equal(0, documents.Calls);
    }

    private static CompletionResponse Answer(string text, string stopReason = "") =>
        new(text, "test-model", new TokenUsage(10, 5), stopReason);

    private static IReadOnlyList<string> Labels(RetrievalResult result) =>
        [.. result.Chunks.Select(chunk => chunk.Source.ChunkId)];

    private static TurnRequest Request(bool quotes = true, bool citations = true, int maxRetries = 1, int maxTokens = 64) =>
        new(
            "how many contracts lapse this quarter?",
            "Context:\n{context}\n\nQ: {query}",
            maxTokens,
            new TurnVerificationChecks(quotes, citations, maxRetries));

    private static TurnOutcome Produced(TurnResult result) =>
        result is TurnOutcome outcome
            ? outcome
            : throw new InvalidOperationException("The turn refused.");

    private static RequiredLayerUnavailable Refused(TurnResult result) =>
        result is RequiredLayerUnavailable refusal
            ? refusal
            : throw new InvalidOperationException("The turn answered.");

    /// <summary>
    /// A model that returns canned answers in order and records what it was asked.
    /// </summary>
    /// <remarks>
    /// The last answer repeats once the list runs out, so a test about a ceiling that keeps being hit does not have to
    /// enumerate every call - while an accidental extra call still shows up in <see cref="Requests"/>.
    /// </remarks>
    private sealed class FakeModel(params CompletionResponse[] answers) : IModelProvider
    {
        private int served;

        public List<CompletionRequest> Requests { get; } = [];

        public ProviderId Id => ProviderId.Local;

        public ValueTask<CompletionResponse> CompleteAsync(
            CompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);

            var answer = answers[Math.Min(served, answers.Length - 1)];
            served++;

            return ValueTask.FromResult(answer);
        }

        /// <summary>Refuses rather than pretending: this double answers completions and nothing else.</summary>
        public ValueTask<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            throw new NotSupportedException("This double answers completions only.");
        }

        /// <summary>Refuses for the same reason <see cref="EmbedAsync"/> does.</summary>
        public ValueTask<ProviderHealth> HealthAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            throw new NotSupportedException("This double answers completions only.");
        }
    }
}
