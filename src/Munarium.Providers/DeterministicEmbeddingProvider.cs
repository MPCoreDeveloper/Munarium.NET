namespace Munarium.Providers;

using System.Text;

/// <summary>
/// An in-process embedding provider: no network, no key, no state, and fully deterministic.
/// </summary>
/// <remarks>
/// It embeds with the hashing trick - each token is hashed into a fixed-width bucket with a sign -
/// then L2-normalises the result. That is a real, if crude, similarity measure: texts sharing
/// vocabulary land closer together. Two properties matter more than quality here. It is
/// <em>reproducible</em>, so an index built with it rebuilds bit-identically and a slice digest
/// stays stable across machines; and it needs nothing external, so the retrieval path can be
/// exercised end to end - including under NativeAOT - without a cloud account or a local model.
/// </remarks>
public sealed class DeterministicEmbeddingProvider : IModelProvider
{
    /// <summary>The model name this provider reports.</summary>
    public const string ModelName = "munarium-deterministic-v1";

    private const int DefaultDimensions = 256;
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    private readonly int _dimensions;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeterministicEmbeddingProvider"/> class.
    /// </summary>
    /// <param name="dimensions">The vector width.</param>
    public DeterministicEmbeddingProvider(int dimensions = DefaultDimensions)
    {
        _dimensions = dimensions > 0
            ? dimensions
            : throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Must be positive.");
    }

    /// <inheritdoc />
    public ProviderId Id => ProviderId.Local;

    /// <summary>Gets the vector width this provider produces.</summary>
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public ValueTask<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The deterministic provider embeds only; use a chat provider for completions.");

    /// <inheritdoc />
    public ValueTask<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var vectors = new ReadOnlyMemory<float>[request.Inputs.Count];

        for (var index = 0; index < request.Inputs.Count; index++)
        {
            vectors[index] = Embed(request.Inputs[index]);
        }

        return ValueTask.FromResult(new EmbeddingResponse(vectors, ModelName, default));
    }

    /// <inheritdoc />
    public ValueTask<ProviderHealth> HealthAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ProviderHealth(true, $"in-process deterministic embedder ({_dimensions} dimensions)"));

    /// <summary>
    /// Embeds one text into a unit-length vector.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <returns>The embedding.</returns>
    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var vector = new float[_dimensions];

        foreach (var token in Tokenize(text))
        {
            var hash = Fnv1a(token);
            var bucket = (int)(hash % (uint)_dimensions);
            vector[bucket] += (hash & 1) == 0 ? 1f : -1f;
        }

        Normalize(vector);
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var token = new StringBuilder();

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    private static uint Fnv1a(string token)
    {
        var hash = FnvOffsetBasis;

        foreach (var value in Encoding.UTF8.GetBytes(token))
        {
            hash = (hash ^ value) * FnvPrime;
        }

        return hash;
    }

    private static void Normalize(float[] vector)
    {
        var sumOfSquares = 0.0;

        foreach (var value in vector)
        {
            sumOfSquares += value * value;
        }

        if (sumOfSquares <= 0)
        {
            return;
        }

        var scale = (float)(1.0 / Math.Sqrt(sumOfSquares));

        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] *= scale;
        }
    }
}
