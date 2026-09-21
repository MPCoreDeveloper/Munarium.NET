namespace Munarium.Providers;

/// <summary>
/// The model names a declaration serves, per tier and per capability.
/// </summary>
/// <remarks>
/// <c>complete</c> and <c>embed</c> are the concrete models this configuration offers, in the order they were written;
/// the three tier overrides are what a runbook's task level resolves through. A tier with no override falls back to the
/// family's built-in model, which is what lets a one-line declaration answer a three-tier runbook.
/// </remarks>
public sealed record ProviderModels
{
    /// <summary>Gets the completion models this declaration serves.</summary>
    public IReadOnlyList<string> Complete { get; init; } = [];

    /// <summary>Gets the embedding models this declaration serves.</summary>
    public IReadOnlyList<string> Embed { get; init; } = [];

    /// <summary>Gets this configuration's override for the fast tier.</summary>
    public string? Fast { get; init; }

    /// <summary>Gets this configuration's override for the capable tier.</summary>
    public string? Capable { get; init; }

    /// <summary>Gets this configuration's override for the frontier tier.</summary>
    public string? Frontier { get; init; }

    /// <summary>Gets the first completion model this configuration names, or <see langword="null"/> when it names none.</summary>
    public string? FirstComplete => Complete.Count > 0 ? Complete[0] : null;

    /// <summary>Gets the first embedding model this configuration names, or <see langword="null"/> when it names none.</summary>
    public string? FirstEmbed => Embed.Count > 0 ? Embed[0] : null;

    /// <summary>Reads the override one tier carries, if any.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The model this configuration pins for it, or <see langword="null"/>.</returns>
    public string? Override(ModelTier tier) => tier switch
    {
        ModelTier.Fast => Fast,
        ModelTier.Capable => Capable,
        ModelTier.Frontier => Frontier,
        _ => null,
    };
}

/// <summary>
/// A provider registration: a declaration, not a secret.
/// </summary>
/// <remarks>
/// Applying a provider configuration records a dialect, an endpoint and the model names that dialect serves - which is
/// all the original's registry row carries, measured rather than assumed. Where the credential lives is named as a
/// location and read by an adapter at the moment of a call.
/// </remarks>
public sealed record ProviderDeclaration
{
    /// <summary>Gets the name this declaration is applied under.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the dialect this declaration speaks.</summary>
    public required ProviderId Provider { get; init; }

    /// <summary>Gets the endpoint override, or <see langword="null"/> for the dialect's own default.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets the models this configuration serves.</summary>
    public ProviderModels Models { get; init; } = new();

    /// <summary>Gets where the credential lives, or <see langword="null"/> for a family that needs none.</summary>
    public CredentialReference? Credential { get; init; }

    /// <summary>Gets the upstream OpenRouter provider slug, when this declaration pins one.</summary>
    public string? OpenRouterProvider { get; init; }

    /// <summary>Gets the rate and token ceilings this configuration is held to.</summary>
    public ProviderBudgets Budgets { get; init; } = ProviderBudgets.None;

    /// <summary>Gets the family this declaration speaks for.</summary>
    public string Family => Provider.Value;

    /// <summary>
    /// Resolves a tier to a concrete model: this configuration's override first, then the family's built-in.
    /// </summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The model, or <see langword="null"/> when neither names one.</returns>
    public string? TierModel(ModelTier tier) =>
        Models.Override(tier) ?? ProviderFamilies.BuiltinTierModel(Family, tier);

    /// <summary>
    /// Refuses a declaration that could never be used, naming why.
    /// </summary>
    /// <remarks>
    /// The refusals the original's own reader makes - an unknown dialect, a cloud family with no credential reference,
    /// a reserved name, an OpenRouter downstream slug on anything but OpenRouter - plus the one a configuration's own
    /// ceilings can carry: a budget that is not a positive number could never be enforced, and one that is declared is
    /// enforced by the relay rather than recorded and forgotten.
    /// </remarks>
    /// <param name="declaration">The declaration.</param>
    /// <returns>The reason, or <see langword="null"/> when the declaration is usable.</returns>
    public static string? Refusal(ProviderDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        return Refusals(declaration).FirstOrDefault(reason => reason is not null);
    }

    /// <summary>
    /// The reasons a declaration could be refused, in the order an author would fix them.
    /// </summary>
    /// <remarks>
    /// A list rather than a ladder of <c>if</c>s so that every rule is visible at once: each entry is the reason when
    /// its rule applies and nothing when it does not, and the first reason wins. The same shape the fact-body validator
    /// reports findings in - a rule a reader can read as a rule.
    /// </remarks>
    /// <param name="declaration">The declaration.</param>
    /// <returns>One entry per rule, in order.</returns>
    private static IEnumerable<string?> Refusals(ProviderDeclaration declaration) =>
    [
        string.IsNullOrWhiteSpace(declaration.Name)
            ? "a provider config needs a name"
            : null,

        string.Equals(declaration.Name, ProviderRegistry.DefaultSelector, StringComparison.Ordinal)
            ? $"the config name '{ProviderRegistry.DefaultSelector}' is reserved for the default-provider rule"
            : null,

        !ProviderFamilies.IsKnown(declaration.Family)
            ? $"unsupported provider '{declaration.Family}' (anthropic|openai|openrouter|ollama)"
            : null,

        ProviderFamilies.NeedsCredential(declaration.Family) && declaration.Credential is null
            ? "credentialRef is required for this provider"
            : null,

        declaration.Credential is { EnvironmentVariable: null or "", FilePath: null or "" }
            ? "credentialRef names neither an environment variable nor a file"
            : null,

        declaration.OpenRouterProvider is { } slug && !ValidSlug(declaration, slug)
            ? "openrouterProvider requires one valid downstream slug on an OpenRouter configuration"
            : null,

        ProviderBudgets.Refusal(declaration.Budgets),
    ];

    /// <summary>Whether an OpenRouter downstream slug is one the original would accept.</summary>
    /// <param name="declaration">The declaration carrying it.</param>
    /// <param name="slug">The slug.</param>
    /// <returns><see langword="true"/> when it is a bounded, slug-shaped name.</returns>
    private static bool ValidSlug(ProviderDeclaration declaration, string slug) =>
        declaration.Family == ProviderFamilies.OpenRouter
        && slug.Length is > 0 and <= 100
        && slug.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '/');
}

/// <summary>A configuration that was refused before it was recorded, with the reason a person reads.</summary>
/// <param name="Reason">What is wrong with it.</param>
public sealed record ProviderConfigRefused(string Reason);

/// <summary>
/// What reading a provider configuration produced: a declaration, or why it could not be read.
/// </summary>
public readonly union ProviderConfigOutcome(ProviderDeclaration, ProviderConfigRefused);

/// <summary>
/// What applying a provider configuration produced: the declaration now in force, or why it was refused.
/// </summary>
public readonly union ProviderDeclarationOutcome(ProviderDeclaration, ProviderConfigRefused);
