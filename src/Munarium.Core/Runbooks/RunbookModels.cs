namespace Munarium.Runbooks;

/// <summary>
/// A provider and model choice.
/// </summary>
/// <remarks>
/// <c>Provider</c> names a tenant provider configuration, <c>Model</c> pins an exact model id, and <c>Tier</c> resolves
/// through that configuration's tier map. At least one has to be present.
/// </remarks>
public sealed record ModelSpec
{
    /// <summary>Gets the provider's name.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the exact model id.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the tier the provider configuration resolves.</summary>
    public string? Tier { get; init; }

    /// <summary>Gets a value indicating whether nothing at all was declared.</summary>
    public bool IsEmpty =>
        Provider is not { Length: > 0 } && Model is not { Length: > 0 } && Tier is not { Length: > 0 };
}

/// <summary>
/// Who may override a runbook's model choices through the API.
/// </summary>
/// <remarks>
/// The document spells this three ways - absent, a flag, or a list of provider names - which is why the port models it as
/// one value with an <see cref="Allowlist"/> rather than as two.
/// </remarks>
public sealed record OverridePolicy
{
    /// <summary>Gets a value indicating whether any configured provider may be requested.</summary>
    public bool All { get; init; }

    /// <summary>Gets the provider names that may be requested, when only some may.</summary>
    public IReadOnlyList<string>? Allowlist { get; init; }

    /// <summary>Gets the policy that rejects every override, which is what an absent block means.</summary>
    public static OverridePolicy None { get; } = new();

    /// <summary>Gets the policy that permits every configured provider.</summary>
    public static OverridePolicy Everything { get; } = new() { All = true };

    /// <summary>
    /// Reports whether a provider may be requested.
    /// </summary>
    /// <param name="provider">The provider's name.</param>
    /// <returns><see langword="true"/> when the policy permits it.</returns>
    public bool Permits(string provider) =>
        All || (Allowlist is { } allowed && allowed.Contains(provider, StringComparer.Ordinal));
}

/// <summary>
/// The model-using task levels a runbook can pin defaults for.
/// </summary>
/// <remarks>
/// Extensible, and unknown keys are validation errors, so a typo fails closed rather than silently doing nothing.
/// </remarks>
public static class TaskLevels
{
    /// <summary>The task that writes an answer.</summary>
    public const string Completion = "completion";

    /// <summary>The task that checks an answer.</summary>
    public const string Validation = "validation";

    /// <summary>The task that embeds text.</summary>
    public const string Embedding = "embedding";

    /// <summary>The task that widens a query before retrieval.</summary>
    public const string QueryExpansion = "query_expansion";

    /// <summary>
    /// The task that resolves what a question is asking, so the hierarchy can pick a profile.
    /// </summary>
    /// <remarks>
    /// This one runs on the server: the semantic plane never calls a model provider.
    /// </remarks>
    public const string Intent = "intent";

    /// <summary>Gets every level, in the order the original declares them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Completion, Validation, Embedding, QueryExpansion, Intent];
}

/// <summary>
/// Default model specification per task level.
/// </summary>
/// <remarks>
/// Resolution order: an API request override when the policy permits it, then <c>Tasks[task]</c>, then
/// <c>Default</c>, then the tenant's provider fallback chain.
/// </remarks>
public sealed record ModelsSpec
{
    /// <summary>Gets the default for every level that does not name its own.</summary>
    public ModelSpec? Default { get; init; }

    /// <summary>Gets the per-level specifications.</summary>
    public IReadOnlyDictionary<string, ModelSpec> Tasks { get; init; } =
        new SortedDictionary<string, ModelSpec>(StringComparer.Ordinal);

    /// <summary>Gets who may override these choices.</summary>
    public OverridePolicy AllowOverrides { get; init; } = OverridePolicy.None;
}

/// <summary>
/// The optional RAG completion for session turns.
/// </summary>
/// <remarks>
/// Retrieval context is interpolated into the template and sent to the resolved completion model.
/// </remarks>
public sealed record CompletionSpec
{
    /// <summary>Gets the shorthand for <c>Models.Tasks.Completion.Provider</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the shorthand for <c>Models.Tasks.Completion.Model</c>.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the template, which has to reference <c>{context}</c> and <c>{query}</c>.</summary>
    public required string PromptTemplate { get; init; }

    /// <summary>Gets the deterministic answer verification, when the turn loop opts into it.</summary>
    public VerificationSpec? Verification { get; init; }

    /// <summary>
    /// Gets how many characters of served context the turn assembles for the model.
    /// </summary>
    /// <remarks>
    /// Hits past the budget are still retrieved and reported, but never reach the prompt: with a <c>topK</c> of 20 over
    /// 1,500-character chunks, about ten of the twenty are served. The cost of widening it is input tokens per turn, so
    /// a runbook that widens retrieval should size this to match.
    /// </remarks>
    public int? ContextCharBudget { get; init; }

    /// <summary>
    /// Gets the ceiling on completion tokens per answer.
    /// </summary>
    /// <remarks>
    /// A ceiling and not spend - a provider bills only what it generates - and the truncation-aware retry still pays one
    /// fourfold re-ask on exhaustion, so the effective ceiling is four times this. It exists because a
    /// reasoning-always-on model draws its hidden reasoning from the same budget and can return empty text at a ceiling
    /// that looks generous.
    /// </remarks>
    public int? MaxTokens { get; init; }
}

/// <summary>
/// The turn-loop verification block.
/// </summary>
/// <remarks>
/// Both checks are deterministic string work over data the turn already holds: no model judges anything.
/// </remarks>
public sealed record VerificationSpec
{
    /// <summary>Gets a value indicating whether quoted spans have to resolve in the served text.</summary>
    public bool Quotes { get; init; }

    /// <summary>Gets a value indicating whether bracketed citations have to name content that was served.</summary>
    public bool Citations { get; init; }

    /// <summary>
    /// Gets how many corrective completions a turn may pay for.
    /// </summary>
    /// <remarks>
    /// Every retry is a paid call, so the count is small and bounded rather than generous.
    /// </remarks>
    public int MaxRetries { get; init; } = 1;
}
