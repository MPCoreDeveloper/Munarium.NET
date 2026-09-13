namespace Munarium.Providers;

/// <summary>
/// The model seam of the Munarium kernel.
/// </summary>
/// <remarks>
/// Providers live behind this interface so the kernel never holds a credential, never picks a
/// vendor, and never talks to a network. A caller can bring any dialect - a hosted API, a local
/// model, or an in-process implementation - and the rest of the kernel cannot tell the difference.
/// </remarks>
public interface IModelProvider
{
    /// <summary>Gets the provider dialect this implementation speaks.</summary>
    ProviderId Id { get; }

    /// <summary>
    /// Generates a completion.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completion.</returns>
    ValueTask<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds texts.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One vector per input, in order.</returns>
    ValueTask<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the provider, so health is observed rather than assumed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The observed health.</returns>
    ValueTask<ProviderHealth> HealthAsync(CancellationToken cancellationToken = default);
}
