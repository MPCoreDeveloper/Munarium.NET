namespace Munarium.Providers;

/// <summary>
/// What one call cost, in tokens.
/// </summary>
/// <param name="InputTokens">Tokens consumed by the request.</param>
/// <param name="OutputTokens">Tokens produced by the response.</param>
public readonly record struct TokenUsage(int InputTokens, int OutputTokens);

/// <summary>
/// A completion request.
/// </summary>
public sealed record CompletionRequest
{
    /// <summary>Gets the model to call.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the system instruction, when the caller has one.</summary>
    public string System { get; init; } = string.Empty;

    /// <summary>Gets the user prompt.</summary>
    public required string Prompt { get; init; }

    /// <summary>Gets the response token ceiling.</summary>
    public int MaxTokens { get; init; } = 1024;

    /// <summary>Gets the sampling temperature.</summary>
    public double Temperature { get; init; }
}

/// <summary>
/// A completion response.
/// </summary>
/// <param name="Text">The generated text.</param>
/// <param name="Model">The model that actually served the request.</param>
/// <param name="Usage">What the call cost.</param>
/// <param name="StopReason">The provider's own stop reason, when its dialect reports one.</param>
public sealed record CompletionResponse(string Text, string Model, TokenUsage Usage, string StopReason = "")
{
    /// <summary>
    /// Gets a value indicating whether the model stopped early rather than finishing.
    /// </summary>
    /// <remarks>
    /// A reasoning model spends hidden tokens from the same completion budget, so it can exhaust the ceiling before any
    /// visible text - which is why an empty answer counts as truncated too. The adapters pass the provider's own stop
    /// reason through verbatim: <c>max_tokens</c> in one dialect, <c>length</c> in another.
    /// </remarks>
    public bool IsTruncated =>
        StopReason is "max_tokens" or "length" || Text.Trim().Length == 0;
}

/// <summary>
/// An embedding request.
/// </summary>
public sealed record EmbeddingRequest
{
    /// <summary>Gets the embedding model to call.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the texts to embed, in order.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }
}

/// <summary>
/// An embedding response.
/// </summary>
/// <param name="Vectors">One vector per input, in the same order.</param>
/// <param name="Model">The model that actually served the request.</param>
/// <param name="Usage">What the call cost.</param>
public sealed record EmbeddingResponse(
    IReadOnlyList<ReadOnlyMemory<float>> Vectors,
    string Model,
    TokenUsage Usage);

/// <summary>
/// A provider's health, as observed rather than assumed.
/// </summary>
/// <param name="Healthy">Whether the provider answered.</param>
/// <param name="Detail">What was observed - key validity, endpoint reachability, or the failure.</param>
/// <param name="EndpointFingerprint">
/// A fingerprint of the endpoint that answered, when the adapter has one. The provider plane reports it so that two
/// configurations pointing at two endpoints are distinguishable without either of them disclosing the endpoint or the
/// key; an adapter that has nothing to say leaves it empty and the registry derives one from the declaration.
/// </param>
public readonly record struct ProviderHealth(
    bool Healthy,
    string Detail,
    string EndpointFingerprint = "");
