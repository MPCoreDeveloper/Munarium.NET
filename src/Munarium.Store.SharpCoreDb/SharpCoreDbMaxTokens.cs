namespace Munarium.Store.SharpCoreDb;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Munarium.Budgets;
using SharpCoreDB.Interfaces;

/// <summary>
/// A tenant's replacement of the process ceilings, in a SharpCoreDB table.
/// </summary>
/// <remarks>
/// A table of its own, because a ceiling is a deployment decision about what its paid calls may cost rather than
/// anything governance reasons over. One row per tenant and the set replaced whole: a partial update would leave an
/// operator unable to say which ceilings are in force.
/// </remarks>
/// <param name="database">The database the ceilings live in.</param>
/// <param name="tableName">The table to hold them in.</param>
public sealed class SharpCoreDbMaxTokens(
    IDatabase database,
    string tableName = SharpCoreDbMaxTokens.DefaultTable) : IMaxTokensStore
{
    /// <summary>The table a deployment gets when it does not choose one.</summary>
    public const string DefaultTable = "munarium_max_tokens";

    private const string Schema =
        "tenant_key TEXT, tenant TEXT, turn_completion LONG, query_expansion LONG, complete_default LONG, "
        + "healthai_probe LONG, hierarchy_classifier LONG, hierarchy_intent LONG, runbook_advisory LONG, "
        + "authoring_assist LONG, updated_at TEXT";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _tableName = TableValues.ValidateTableName(tableName);

    /// <inheritdoc />
    public ValueTask<MaxTokensReplacement?> FindAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var rows = Table().Select(TableValues.Identity("tenant_key", Key(tenant))).ToList();

            if (rows.Count == 0)
            {
                return ValueTask.FromResult<MaxTokensReplacement?>(null);
            }

            var row = rows[0];

            // The tenant is caller-supplied text and a predicate here compares an identity and nothing else (measured),
            // so the row's own tenant is checked after the read.
            return string.Equals(TableValues.StringValue(row, "tenant"), tenant, StringComparison.Ordinal)
                ? ValueTask.FromResult<MaxTokensReplacement?>(new MaxTokensReplacement(
                    Budgets(row),
                    TableValues.StringValue(row, "updated_at")))
                : ValueTask.FromResult<MaxTokensReplacement?>(null);
        }
    }

    /// <inheritdoc />
    public ValueTask<MaxTokensReplacement> ReplaceAsync(
        string tenant,
        MaxTokensBudget budgets,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(budgets);
        cancellationToken.ThrowIfCancellationRequested();

        var updatedAt = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            var table = Table();

            // One row per tenant: a set of ceilings is replaced whole, and a second row beside the first would be a set
            // nobody could say the truth about.
            table.Delete(TableValues.Identity("tenant_key", Key(tenant)));
            table.Insert(new Dictionary<string, object>
            {
                ["tenant_key"] = Key(tenant),
                ["tenant"] = tenant,
                ["turn_completion"] = budgets.TurnCompletion,
                ["query_expansion"] = budgets.QueryExpansion,
                ["complete_default"] = budgets.CompleteDefault,
                ["healthai_probe"] = budgets.HealthAiProbe,
                ["hierarchy_classifier"] = budgets.HierarchyClassifier,
                ["hierarchy_intent"] = budgets.HierarchyIntent,
                ["runbook_advisory"] = budgets.RunbookAdvisory,
                ["authoring_assist"] = budgets.AuthoringAssist,
                ["updated_at"] = updatedAt,
            });

            Persist();

            return ValueTask.FromResult(new MaxTokensReplacement(budgets, updatedAt));
        }
    }

    /// <summary>Reads the eight ceilings out of a row.</summary>
    /// <param name="row">The stored columns.</param>
    /// <returns>The ceilings.</returns>
    private static MaxTokensBudget Budgets(Dictionary<string, object> row) => new(
        (int)TableValues.LongValue(row, "turn_completion"),
        (int)TableValues.LongValue(row, "query_expansion"),
        (int)TableValues.LongValue(row, "complete_default"),
        (int)TableValues.LongValue(row, "healthai_probe"),
        (int)TableValues.LongValue(row, "hierarchy_classifier"),
        (int)TableValues.LongValue(row, "hierarchy_intent"),
        (int)TableValues.LongValue(row, "runbook_advisory"),
        (int)TableValues.LongValue(row, "authoring_assist"));

    /// <summary>Derives the key a tenant's row is addressed by.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The key.</returns>
    private static string Key(string tenant) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenant)))[..16];

    private ITable Table()
    {
        if (_database.TryGetTable(_tableName, out var existing))
        {
            return existing;
        }

        _database.ExecuteSQL($"CREATE TABLE {_tableName} ({Schema})");

        return _database.TryGetTable(_tableName, out var created)
            ? created
            : throw new InvalidOperationException(
                $"the table '{_tableName}' could not be created, so no ceiling can be kept");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
