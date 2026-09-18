namespace Munarium.Core.Tests.Sessions;

using Munarium.Providers;
using Munarium.Runbooks;
using Munarium.Sessions;

/// <summary>
/// Tests for the intent task: what a runbook that pins none gets, what a classification is allowed to be, and what an
/// unusable answer becomes.
/// </summary>
public class IntentResolutionTests
{
    /// <summary>
    /// A runbook that pins no intent task gets an intent that says nothing was modelled, and no model is asked - the
    /// document path is a complete answer, not a degraded one.
    /// </summary>
    [Fact]
    public async Task ARunbookWithoutTheTaskAsksNobodyAnything()
    {
        var model = new RecordingModel("aggregation");

        var intent = await IntentResolution.ResolveAsync(Document(pinned: false), "how many lapse?", model, "test-model");

        Assert.True(intent.Explicit);
        Assert.Null(intent.Kind);
        Assert.Equal("how many lapse?", intent.Question);
        Assert.Empty(model.Requests);
        Assert.False(IntentResolution.IsPinned(Document(pinned: false)));
        Assert.True(IntentResolution.IsPinned(Document(pinned: true)));
    }

    /// <summary>
    /// A pinned task is asked to classify rather than to answer, at temperature zero, and the prompt lists the closed
    /// vocabulary so the answer can be judged against it.
    /// </summary>
    [Fact]
    public async Task APinnedTaskIsAskedToOneOfSixWordsAtTemperatureZero()
    {
        var model = new RecordingModel("Aggregation.");

        var intent = await IntentResolution.ResolveAsync(Document(pinned: true), "how many lapse?", model, "small-model");

        var sent = Assert.Single(model.Requests);
        Assert.Equal("small-model", sent.Model);
        Assert.Equal(0.0, sent.Temperature);
        Assert.Equal(IntentResolution.DefaultMaxTokens, sent.MaxTokens);
        Assert.Contains("Do not answer the question itself.", sent.Prompt, StringComparison.Ordinal);
        Assert.Contains("Question: how many lapse?", sent.Prompt, StringComparison.Ordinal);
        Assert.Equal("aggregation", intent.Kind);
        Assert.False(intent.Explicit);
    }

    /// <summary>
    /// The vocabulary is closed: a kind nothing downstream understands becomes no kind rather than being passed
    /// through, because it would look like information.
    /// </summary>
    [Theory]
    [InlineData("The question is an aggregation of contracts.", "aggregation")]
    [InlineData("TIMELINE", "timeline")]
    [InlineData("other", "other")]
    [InlineData("I am sorry, I cannot help with that.", null)]
    [InlineData("", null)]
    public void AnUnrecognisedAnswerBecomesNoKindAtAll(string answer, string? expected)
    {
        Assert.Equal(expected, IntentResolution.ParseKind(answer));

        var intent = IntentResolution.Classify("q", answer);

        Assert.Equal(expected, intent.Kind);
        Assert.False(intent.Explicit);
    }

    private static RunbookDocument Document(bool pinned) => new()
    {
        ApiVersion = "munarium.dev/v2",
        Kind = "Runbook",
        Metadata = new RunbookMeta { Name = "northgate", Version = 3 },
        Spec = new RunbookSpec
        {
            Collections = [new CollectionSpec { Name = "contracts", Shape = "cuad-contracts@3" }],
            Models = pinned
                ? new ModelsSpec { Tasks = new Dictionary<string, ModelSpec> { [TaskLevels.Intent] = new() { Model = "small-model" } } }
                : null,
        },
    };

    private sealed class RecordingModel(string answer) : IModelProvider
    {
        public List<CompletionRequest> Requests { get; } = [];

        public ProviderId Id => ProviderId.Local;

        public ValueTask<CompletionResponse> CompleteAsync(
            CompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);

            return ValueTask.FromResult(new CompletionResponse(answer, request.Model, new TokenUsage(5, 1)));
        }

        public ValueTask<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            throw new NotSupportedException("This double classifies only.");
        }

        public ValueTask<ProviderHealth> HealthAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            throw new NotSupportedException("This double classifies only.");
        }
    }
}
