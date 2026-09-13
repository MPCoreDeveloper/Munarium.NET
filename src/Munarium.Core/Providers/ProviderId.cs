namespace Munarium.Providers;

/// <summary>
/// Identifies a model provider dialect.
/// </summary>
/// <remarks>
/// The provider brings its own credentials and endpoint; the kernel only ever knows this identity
/// and the seam behind it.
/// </remarks>
/// <param name="Value">The provider's stable name.</param>
public sealed record ProviderId(string Value)
{
    /// <summary>Anthropic (Claude) models.</summary>
    public static readonly ProviderId Anthropic = new("anthropic");

    /// <summary>OpenAI models, including OpenAI-compatible endpoints.</summary>
    public static readonly ProviderId OpenAi = new("openai");

    /// <summary>OpenRouter, which fronts many upstream models.</summary>
    public static readonly ProviderId OpenRouter = new("openrouter");

    /// <summary>A local Ollama endpoint.</summary>
    public static readonly ProviderId Ollama = new("ollama");

    /// <summary>An in-process provider with no network and no key, such as the deterministic embedder.</summary>
    public static readonly ProviderId Local = new("local");

    /// <inheritdoc />
    public override string ToString() => Value;
}
