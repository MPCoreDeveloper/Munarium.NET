namespace Munarium.Budgets;

using System.Globalization;

/// <summary>
/// The ceilings a deployment's paid calls are held to: a tenant's replacement first, the process defaults behind it.
/// </summary>
/// <remarks>
/// Read where the call is made rather than cached beside it. The original caches with a TTL because a config applied on
/// another replica has to converge here; this port's deployment is one process by decision, so the store is read and the
/// answer cannot be stale.
/// </remarks>
/// <param name="store">Where a tenant's replacement is kept.</param>
/// <param name="processDefaults">The process's own ceilings, normally the built-ins with the environment over them.</param>
public sealed class MaxTokensCeiling(IMaxTokensStore store, MaxTokensBudget processDefaults)
{
    /// <summary>The source reported while a tenant's own replacement applies.</summary>
    public const string TenantSource = "tenant";

    /// <summary>The source reported while the process defaults apply.</summary>
    public const string EnvironmentSource = "environment";

    private readonly IMaxTokensStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly MaxTokensBudget _processDefaults =
        processDefaults ?? throw new ArgumentNullException(nameof(processDefaults));

    /// <summary>The ceilings this process was composed with: its built-ins with its own variables over them.</summary>
    /// <param name="store">Where a tenant's replacement is kept.</param>
    /// <returns>The ceiling.</returns>
    public static MaxTokensCeiling Process(IMaxTokensStore store) =>
        new(store, MaxTokensBudget.Between(MaxTokensBudget.Builtin, Environment.GetEnvironmentVariable));

    /// <summary>Gets the ceilings that apply while no tenant has replaced them.</summary>
    public MaxTokensBudget ProcessDefaults => _processDefaults;

    /// <summary>Reads what applies to one tenant right now.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective ceilings, and where they came from.</returns>
    public async ValueTask<MaxTokensResolution> EffectiveAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        var stored = await _store.FindAsync(tenant, cancellationToken).ConfigureAwait(false);

        return stored is null
            ? new MaxTokensResolution(_processDefaults, EnvironmentSource, UpdatedAt: null)
            : new MaxTokensResolution(stored.Budgets, TenantSource, stored.UpdatedAt);
    }

    /// <summary>Replaces one tenant's ceilings.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="budgets">The ceilings that now apply.</param>
    /// <param name="now">When the replacement is made.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective ceilings after the write.</returns>
    public async ValueTask<MaxTokensResolution> ReplaceAsync(
        string tenant,
        MaxTokensBudget budgets,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var replaced = await _store
            .ReplaceAsync(tenant, budgets, now.ToUniversalTime(), cancellationToken)
            .ConfigureAwait(false);

        return new MaxTokensResolution(replaced.Budgets, TenantSource, replaced.UpdatedAt);
    }

    /// <summary>Formats an instant the way the contract carries one.</summary>
    /// <param name="moment">The instant.</param>
    /// <returns>The instant, as RFC 3339 in UTC.</returns>
    public static string Timestamp(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
