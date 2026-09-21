namespace Munarium.Core.Tests.Support;

using Munarium.Providers;

/// <summary>
/// An adapter a test composes: one dialect, what it answers, and what it fails with.
/// </summary>
/// <remarks>
/// Deliberately not the deterministic embedder: a provider plane is probed with a small completion and a health call,
/// so the double has to answer those. <paramref name="failure"/> makes a probe fail on purpose, which is the case that
/// proves a failed probe is a check rather than a failure of the plane.
/// </remarks>
/// <param name="family">The family this adapter speaks for.</param>
/// <param name="answer">What a completion answers with.</param>
/// <param name="fingerprint">What the health probe reports as its endpoint fingerprint.</param>
/// <param name="failure">What a call throws, or <see langword="null"/> to answer.</param>
/// <param name="dimensions">How wide the vectors an embedding answers with are.</param>
internal sealed class ProbeProvider(
    string family,
    string answer = "OK",
    string fingerprint = "",
    Exception? failure = null,
    int dimensions = 4) : IModelProvider
{
    /// <summary>Gets the completions this adapter was asked for.</summary>
    public List<CompletionRequest> Completions { get; } = [];

    /// <summary>Gets the embeddings this adapter was asked for.</summary>
    public List<EmbeddingRequest> Embeddings { get; } = [];

    /// <inheritdoc />
    public ProviderId Id => new(family);

    /// <inheritdoc />
    public ValueTask<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A probe that throws is the case worth having on purpose, so the double fails when it is told to.
        return failure is null
            ? ValueTask.FromResult(AnswerTo(request))
            : throw failure;
    }

    /// <summary>Records the call and answers it.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The completion.</returns>
    private CompletionResponse AnswerTo(CompletionRequest request)
    {
        Completions.Add(request);

        return new CompletionResponse(answer, request.Model, new TokenUsage(1, 1), "end_turn");
    }

    /// <inheritdoc />
    public ValueTask<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (failure is not null)
        {
            throw failure;
        }

        Embeddings.Add(request);

        return ValueTask.FromResult(
            new EmbeddingResponse(
                [.. request.Inputs.Select(_ => (ReadOnlyMemory<float>)Enumerable.Repeat(1f, dimensions).ToArray())],
                request.Model,
                new TokenUsage(request.Inputs.Count * 4, 0)));
    }

    /// <inheritdoc />
    public ValueTask<ProviderHealth> HealthAsync(CancellationToken cancellationToken = default) =>
        failure is null
            ? ValueTask.FromResult(new ProviderHealth(true, $"{family} answered", fingerprint))
            : throw failure;
}
