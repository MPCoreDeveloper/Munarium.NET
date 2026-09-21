namespace Munarium.Budgets;

/// <summary>
/// Where a tenant's replacement of the process ceilings is kept.
/// </summary>
/// <remarks>
/// A seam, because the ceiling is read before every paid call and written by an operator: the read has to be cheap and
/// the write has to outlive the process that took it. This port's deployment is one node by decision, so the store is
/// the deployment's own database rather than a ledger shared across replicas.
/// </remarks>
public interface IMaxTokensStore
{
    /// <summary>Reads the tenant's replacement, when one was made.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The replacement, or <see langword="null"/> while the process defaults apply.</returns>
    ValueTask<MaxTokensReplacement?> FindAsync(string tenant, CancellationToken cancellationToken = default);

    /// <summary>Replaces the tenant's ceilings in one write, because a set is replaced whole.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="budgets">The ceilings that now apply.</param>
    /// <param name="now">When the replacement was made.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The replacement as it now stands.</returns>
    ValueTask<MaxTokensReplacement> ReplaceAsync(
        string tenant,
        MaxTokensBudget budgets,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>A tenant's replacement of the process ceilings, with the instant it was made.</summary>
/// <param name="Budgets">The ceilings that apply.</param>
/// <param name="UpdatedAt">When the replacement was made, as the contract carries it.</param>
public sealed record MaxTokensReplacement(MaxTokensBudget Budgets, string UpdatedAt);

/// <summary>What a deployment's ceilings are right now, and where they came from.</summary>
/// <param name="Budgets">The effective ceilings.</param>
/// <param name="Source">Whether a tenant replaced them, or the process defaults apply.</param>
/// <param name="UpdatedAt">When a tenant replaced them, absent while the process defaults apply.</param>
public readonly record struct MaxTokensResolution(MaxTokensBudget Budgets, string Source, string? UpdatedAt);
