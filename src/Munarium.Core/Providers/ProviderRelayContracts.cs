namespace Munarium.Providers;

/// <summary>
/// A relayed completion: what a caller asks a deployment to spend its own credential on.
/// </summary>
/// <remarks>
/// The model resolves in the original's order - an explicit model, then the tier, then the configuration's first
/// completion model, then the family's capable built-in - and the ceiling is the caller's where it names one and the
/// deployment's <c>complete_default</c> where it does not.
/// </remarks>
public sealed record ProviderCompletionQuery
{
    /// <summary>Gets the model to call, which wins over the tier when both are named.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the tier whose model to call.</summary>
    public ModelTier? Tier { get; init; }

    /// <summary>Gets the system instruction, when the caller has one.</summary>
    public string? System { get; init; }

    /// <summary>Gets the user prompt.</summary>
    public required string Prompt { get; init; }

    /// <summary>Gets the output ceiling the caller names, or <see langword="null"/> for the deployment's own.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Gets the sampling temperature.</summary>
    public double? Temperature { get; init; }

    /// <summary>Gets the family override, which only the reserved <c>default</c> config name honours.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the version an invocation would be recorded against, which this port refuses by name.</summary>
    public string? VersionId { get; init; }
}

/// <summary>Relayed embedding: texts in, one vector per text out.</summary>
public sealed record ProviderEmbeddingQuery
{
    /// <summary>Gets the embedding model to call.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the texts to embed, which have to be at least one.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>Gets the family override, which only the reserved <c>default</c> config name honours.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the version an invocation would be recorded against, which this port refuses by name.</summary>
    public string? VersionId { get; init; }
}

/// <summary>What a relayed completion produced.</summary>
/// <param name="Provider">The family that served it.</param>
/// <param name="Text">The generated text.</param>
/// <param name="Model">The model that served it.</param>
/// <param name="StopReason">The dialect's own stop reason.</param>
/// <param name="InputTokens">What the request cost.</param>
/// <param name="OutputTokens">What the answer cost.</param>
public sealed record ProviderCompletion(
    string Provider,
    string Text,
    string Model,
    string StopReason,
    long InputTokens,
    long OutputTokens);

/// <summary>What a relayed embedding produced.</summary>
/// <param name="Provider">The family that served it.</param>
/// <param name="Vectors">One vector per input, in the same order.</param>
/// <param name="Model">The model that served it.</param>
/// <param name="Dimensions">How wide the vectors are.</param>
/// <param name="CacheHit">Whether it came from a cache - always false here, because this port keeps none.</param>
public sealed record ProviderEmbedding(
    string Provider,
    IReadOnlyList<IReadOnlyList<float>> Vectors,
    string Model,
    int Dimensions,
    bool CacheHit);

/// <summary>
/// A relayed call that was refused, with the status it is refused with.
/// </summary>
/// <remarks>
/// The statuses are the original's own: invalid input is 400, a configuration this deployment does not hold is 404, a
/// ceiling that is reached is 429, and a plane that cannot be called at all - no credential where one is named, no
/// adapter for the family, no usable family at all, or a provider that failed - is 502, because the caller's request was
/// sound and the deployment's dependency was not.
/// </remarks>
/// <param name="Reason">What is wrong, written for the caller.</param>
/// <param name="Status">The status the refusal is carried with.</param>
public sealed record ProviderCallRefused(string Reason, int Status)
{
    /// <summary>What was asked for cannot be honoured as written.</summary>
    public const int InvalidInput = 400;

    /// <summary>Nothing is held under that configuration name.</summary>
    public const int UnknownConfiguration = 404;

    /// <summary>A ceiling the configuration declared is reached.</summary>
    public const int RateLimited = 429;

    /// <summary>The deployment cannot call that family right now.</summary>
    public const int Unavailable = 502;
}

/// <summary>What relaying a completion produced: the answer, or why it was refused.</summary>
public readonly union ProviderCompletionOutcome(ProviderCompletion, ProviderCallRefused);

/// <summary>What relaying an embedding produced: the vectors, or why it was refused.</summary>
public readonly union ProviderEmbeddingOutcome(ProviderEmbedding, ProviderCallRefused);
