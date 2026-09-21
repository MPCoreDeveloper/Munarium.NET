namespace Munarium.Providers;

/// <summary>
/// What every provider family carries that is not a deployment's to decide.
/// </summary>
/// <remarks>
/// Four families are known, and three of them are reachable with a credential; the fourth is a local endpoint. The
/// built-in tier models and the conventional environment variable names are the original's, ported verbatim, because a
/// declaration that named no model still has to resolve to a concrete one and an operator has to know which variable to
/// set. Nothing here is a credential: a name is not a secret, and the value is read at call time and never stored.
/// </remarks>
public static class ProviderFamilies
{
    /// <summary>Anthropic (Claude) models.</summary>
    public const string Anthropic = "anthropic";

    /// <summary>OpenAI models, including OpenAI-compatible endpoints.</summary>
    public const string OpenAi = "openai";

    /// <summary>OpenRouter, which fronts many upstream models.</summary>
    public const string OpenRouter = "openrouter";

    /// <summary>A local Ollama endpoint, which needs no credential.</summary>
    public const string Ollama = "ollama";

    /// <summary>
    /// The order the default rule tries families in: the first with a usable credential wins.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPriority = [Anthropic, OpenAi, OpenRouter];

    /// <summary>Whether a name is a family this port knows.</summary>
    /// <param name="family">The name a document or a caller used.</param>
    /// <returns><see langword="true"/> for the four known families.</returns>
    public static bool IsKnown(string family) =>
        family is Anthropic or OpenAi or OpenRouter or Ollama;

    /// <summary>Whether a family needs a credential at all.</summary>
    /// <param name="family">The family.</param>
    /// <returns><see langword="true"/> for every family but the local endpoint.</returns>
    public static bool NeedsCredential(string family) =>
        IsKnown(family) && !string.Equals(family, Ollama, StringComparison.Ordinal);

    /// <summary>
    /// The model a tier resolves to when the declaration overrides nothing.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="tier">The tier.</param>
    /// <returns>The model, or <see langword="null"/> for a family whose tiers this port does not know.</returns>
    public static string? BuiltinTierModel(string family, ModelTier tier) => (family, tier) switch
    {
        (Anthropic, ModelTier.Fast) => "claude-haiku-4-5",
        (Anthropic, ModelTier.Capable) => "claude-sonnet-5",
        (Anthropic, ModelTier.Frontier) => "claude-fable-5-1",
        (OpenAi, ModelTier.Fast) => "gpt-5.4-mini",
        (OpenAi, ModelTier.Capable) => "gpt-5.4",
        (OpenAi, ModelTier.Frontier) => "gpt-5.6-sol",
        (OpenRouter, ModelTier.Fast) => "deepseek/deepseek-v4-flash",
        (OpenRouter, ModelTier.Capable) => "z-ai/glm-5.2",
        (OpenRouter, ModelTier.Frontier) => "z-ai/glm-5.3",
        _ => null,
    };

    /// <summary>
    /// The conventional environment variable carrying a family's default credential.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <returns>The variable's name, or <see langword="null"/> for a family that has none.</returns>
    public static string? DefaultEnvironmentVariable(string family) => family switch
    {
        Anthropic => "MUNARIUM_SECRET_ANTHROPIC",
        OpenAi => "MUNARIUM_SECRET_OPENAI",
        OpenRouter => "MUNARIUM_SECRET_OPENROUTER",
        _ => null,
    };

    /// <summary>The name a family's synthesized, environment-backed default is listed under.</summary>
    /// <param name="family">The family.</param>
    /// <returns>The name, as the original spells it.</returns>
    public static string DefaultName(string family) => $"default-{family}";
}
