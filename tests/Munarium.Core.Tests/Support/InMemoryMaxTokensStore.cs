namespace Munarium.Core.Tests.Support;

using Munarium.Budgets;

/// <summary>
/// A tenant's replacement of the process ceilings, held in memory for a test.
/// </summary>
/// <remarks>
/// The seam rather than a real table: what these tests are about is which ceilings apply and where they came from, and
/// the table's own round trip is tested where the table is.
/// </remarks>
internal sealed class InMemoryMaxTokensStore : IMaxTokensStore
{
    private readonly Dictionary<string, MaxTokensReplacement> _held = [];

    /// <inheritdoc />
    public ValueTask<MaxTokensReplacement?> FindAsync(
        string tenant,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_held.TryGetValue(tenant, out var replacement) ? replacement : null);

    /// <inheritdoc />
    public ValueTask<MaxTokensReplacement> ReplaceAsync(
        string tenant,
        MaxTokensBudget budgets,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var replacement = new MaxTokensReplacement(
            budgets,
            now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));

        _held[tenant] = replacement;

        return ValueTask.FromResult(replacement);
    }
}
