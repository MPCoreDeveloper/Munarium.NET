namespace Munarium.Core.Tests.Support;

using Munarium.Providers;

/// <summary>
/// An <see cref="IModelProvider"/> that records what it was asked to embed and answers with identifiable vectors.
/// </summary>
/// <remarks>
/// The vectors carry the input's ordinal, so a test can prove that a vector landed on the text it was computed from -
/// which is the failure this seam has to make visible, because a provider that returned fewer vectors than inputs
/// would otherwise have its vectors paired with the wrong chunks. <paramref name="vectorCount"/> exists to make that
/// failure happen on purpose.
/// </remarks>
/// <param name="dimensions">The vector width to answer with.</param>
/// <param name="vectorCount">How many vectors to answer with, or <see langword="null"/> to match the inputs.</param>
internal sealed class RecordingEmbeddingProvider(int dimensions = 4, int? vectorCount = null) : IModelProvider
{
    private readonly int? _vectorCount = vectorCount;

    /// <summary>Gets the requests this provider was asked to serve.</summary>
    public List<EmbeddingRequest> Requests { get; } = [];

    /// <inheritdoc />
    public ProviderId Id => ProviderId.Local;

    /// <inheritdoc />
    public ValueTask<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The recording provider embeds only.");

    /// <inheritdoc />
    public ValueTask<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Requests.Add(request);

        var count = _vectorCount ?? request.Inputs.Count;
        var vectors = new ReadOnlyMemory<float>[count];

        for (var index = 0; index < count; index++)
        {
            vectors[index] = Enumerable.Repeat((float)(index + 1), dimensions).ToArray();
        }

        return ValueTask.FromResult(
            new EmbeddingResponse(vectors, request.Model, new TokenUsage(0, 0)));
    }

    /// <inheritdoc />
    public ValueTask<ProviderHealth> HealthAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ProviderHealth(true, "in-process"));
}
