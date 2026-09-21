namespace Munarium.Providers;

/// <summary>
/// The rate and token ceilings a provider configuration declares.
/// </summary>
/// <remarks>
/// The original's own three: requests and tokens per minute, and a daily token ceiling per tier. A configuration that
/// declares none is unlimited, which is the house rule that an undecided policy defaults to off.
/// </remarks>
public sealed record ProviderBudgets
{
    /// <summary>Gets the requests this configuration may serve per minute.</summary>
    public int? RequestsPerMinute { get; init; }

    /// <summary>Gets the tokens this configuration may spend per minute.</summary>
    public int? TokensPerMinute { get; init; }

    /// <summary>Gets the daily token ceiling for the fast tier.</summary>
    public long? Fast { get; init; }

    /// <summary>Gets the daily token ceiling for the capable tier.</summary>
    public long? Capable { get; init; }

    /// <summary>Gets the daily token ceiling for the frontier tier.</summary>
    public long? Frontier { get; init; }

    /// <summary>No ceilings at all, which is what a configuration that declares none carries.</summary>
    public static ProviderBudgets None { get; } = new();

    /// <summary>Whether this declares no ceiling at all.</summary>
    public bool IsEmpty =>
        RequestsPerMinute is null
        && TokensPerMinute is null
        && Fast is null
        && Capable is null
        && Frontier is null;

    /// <summary>Reads the daily ceiling one tier carries.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The ceiling, or <see langword="null"/> when that tier is unlimited.</returns>
    public long? Daily(ModelTier tier) => tier switch
    {
        ModelTier.Fast => Fast,
        ModelTier.Capable => Capable,
        ModelTier.Frontier => Frontier,
        _ => null,
    };

    /// <summary>Refuses a declared budget that could not be enforced as written.</summary>
    /// <param name="budgets">The budget.</param>
    /// <returns>The reason, or <see langword="null"/> when every ceiling is usable.</returns>
    public static string? Refusal(ProviderBudgets budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);

        return Refusals(budgets).FirstOrDefault(reason => reason is not null);
    }

    /// <summary>One entry per rule a declared budget has to satisfy, in the order an operator would fix them.</summary>
    /// <param name="budgets">The budget.</param>
    /// <returns>The reason each rule gives, or nothing when it holds.</returns>
    private static IEnumerable<string?> Refusals(ProviderBudgets budgets) =>
    [
        budgets.RequestsPerMinute is <= 0 ? "budgets.rpm must be a positive number of requests" : null,
        budgets.TokensPerMinute is <= 0 ? "budgets.tpm must be a positive number of tokens" : null,
        budgets.Fast is <= 0 || budgets.Capable is <= 0 || budgets.Frontier is <= 0
            ? "budgets.dailyTokens must be positive numbers of tokens"
            : null,
    ];
}
