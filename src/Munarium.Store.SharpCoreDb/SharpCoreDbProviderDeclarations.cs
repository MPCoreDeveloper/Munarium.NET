namespace Munarium.Store.SharpCoreDb;

using System.Security.Cryptography;
using System.Text;
using Munarium.Providers;
using SharpCoreDB.Interfaces;

/// <summary>
/// Applied provider declarations, held in a SharpCoreDB table.
/// </summary>
/// <remarks>
/// A table of its own rather than a corner of the ledger: a declaration is a deployment fact about which dialect and
/// which models this process may call, not something governance reasons over. A table rather than a dictionary because
/// a declaration a restart forgets is a plane nobody can rely on - an operator who applies one today expects the
/// deployment that comes back to still know it.
/// <para>
/// The row carries no credential and no credential material: an environment variable's name, or a file's path, and
/// whether it resolves is asked of the environment at read time rather than stored.
/// </para>
/// </remarks>
/// <param name="database">The database the declarations live in.</param>
/// <param name="tableName">The table to hold them in.</param>
public sealed class SharpCoreDbProviderDeclarations(
    IDatabase database,
    string tableName = SharpCoreDbProviderDeclarations.DefaultTable) : IProviderDeclarations
{
    /// <summary>The table a deployment gets when it does not choose one.</summary>
    public const string DefaultTable = "munarium_providers";

    private const char Separator = '\n';

    private const string Schema =
        "provider_key TEXT, tenant TEXT, name TEXT, family TEXT, endpoint TEXT, models_complete TEXT, "
        + "models_embed TEXT, fast TEXT, capable TEXT, frontier TEXT, credential_env TEXT, credential_file TEXT, "
        + "openrouter_provider TEXT, rpm LONG, tpm LONG, daily_fast LONG, daily_capable LONG, daily_frontier LONG";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _tableName = TableValues.ValidateTableName(tableName);

    /// <inheritdoc />
    public ValueTask<ProviderDeclaration> SaveAsync(
        string tenant,
        ProviderDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(declaration);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var table = Table();

            // Applying the same name again replaces the declaration, which is how an operator changes an endpoint or
            // a key's location without a second row standing beside the first and disagreeing with it.
            table.Delete(TableValues.Identity("provider_key", Key(tenant, declaration.Name)));
            table.Insert(Row(tenant, declaration));
            Persist();

            return ValueTask.FromResult(declaration);
        }
    }

    /// <inheritdoc />
    public ValueTask<ProviderDeclaration?> FindAsync(
        string tenant,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var rows = Table().Select(TableValues.Identity("provider_key", Key(tenant, name))).ToList();

            if (rows.Count == 0)
            {
                return ValueTask.FromResult<ProviderDeclaration?>(null);
            }

            var declaration = Map(rows[0]);

            // The name is a caller's word, so the row's own name is checked after the read: a digest collision would
            // otherwise serve one configuration's models under another's name.
            return ValueTask.FromResult<ProviderDeclaration?>(
                string.Equals(declaration.Name, name, StringComparison.Ordinal) ? declaration : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ProviderDeclaration>> ListAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // The tenant is caller-supplied text and a predicate here compares identities only (measured), so it is
            // compared after the read - on the rows a deployment holds, which is operator scale.
            return ValueTask.FromResult<IReadOnlyList<ProviderDeclaration>>(
            [
                .. Table().Select()
                    .Where(row => string.Equals(TableValues.StringValue(row, "tenant"), tenant, StringComparison.Ordinal))
                    .Select(Map)
                    .OrderBy(declaration => declaration.Name, StringComparer.Ordinal),
            ]);
        }
    }

    /// <summary>Reads a row into a declaration.</summary>
    /// <param name="row">The stored columns.</param>
    /// <returns>The declaration.</returns>
    private static ProviderDeclaration Map(Dictionary<string, object> row) => new()
    {
        Name = TableValues.StringValue(row, "name"),
        Provider = new ProviderId(TableValues.StringValue(row, "family")),
        Endpoint = Optional(TableValues.StringValue(row, "endpoint")),
        Models = new ProviderModels
        {
            Complete = Models(TableValues.StringValue(row, "models_complete")),
            Embed = Models(TableValues.StringValue(row, "models_embed")),
            Fast = Optional(TableValues.StringValue(row, "fast")),
            Capable = Optional(TableValues.StringValue(row, "capable")),
            Frontier = Optional(TableValues.StringValue(row, "frontier")),
        },
        Credential = Credential(
            TableValues.StringValue(row, "credential_env"),
            TableValues.StringValue(row, "credential_file")),
        OpenRouterProvider = Optional(TableValues.StringValue(row, "openrouter_provider")),
        Budgets = new ProviderBudgets
        {
            RequestsPerMinute = Whole(row, "rpm"),
            TokensPerMinute = Whole(row, "tpm"),
            Fast = Daily(row, "daily_fast"),
            Capable = Daily(row, "daily_capable"),
            Frontier = Daily(row, "daily_frontier"),
        },
    };

    /// <summary>Writes a declaration's columns, without a credential anywhere among them.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="declaration">The declaration.</param>
    /// <returns>The row.</returns>
    private static Dictionary<string, object> Row(string tenant, ProviderDeclaration declaration) => new()
    {
        ["provider_key"] = Key(tenant, declaration.Name),
        ["tenant"] = tenant,
        ["name"] = declaration.Name,
        ["family"] = declaration.Family,
        ["endpoint"] = declaration.Endpoint ?? string.Empty,
        ["models_complete"] = string.Join(Separator, declaration.Models.Complete),
        ["models_embed"] = string.Join(Separator, declaration.Models.Embed),
        ["fast"] = declaration.Models.Fast ?? string.Empty,
        ["capable"] = declaration.Models.Capable ?? string.Empty,
        ["frontier"] = declaration.Models.Frontier ?? string.Empty,
        ["credential_env"] = declaration.Credential?.EnvironmentVariable ?? string.Empty,
        ["credential_file"] = declaration.Credential?.FilePath ?? string.Empty,
        ["openrouter_provider"] = declaration.OpenRouterProvider ?? string.Empty,
        ["rpm"] = declaration.Budgets.RequestsPerMinute ?? 0,
        ["tpm"] = declaration.Budgets.TokensPerMinute ?? 0,
        ["daily_fast"] = declaration.Budgets.Fast ?? 0,
        ["daily_capable"] = declaration.Budgets.Capable ?? 0,
        ["daily_frontier"] = declaration.Budgets.Frontier ?? 0,
    };

    /// <summary>Reads a whole-number ceiling out of a row, where zero means the declaration named none.</summary>
    /// <param name="row">The stored columns.</param>
    /// <param name="column">The column.</param>
    /// <returns>The ceiling, or <see langword="null"/>.</returns>
    private static int? Whole(Dictionary<string, object> row, string column) =>
        TableValues.LongValue(row, column) is > 0 and <= int.MaxValue and var value ? (int)value : null;

    /// <summary>Reads a daily token ceiling out of a row, where zero means unlimited.</summary>
    /// <param name="row">The stored columns.</param>
    /// <param name="column">The column.</param>
    /// <returns>The ceiling, or <see langword="null"/>.</returns>
    private static long? Daily(Dictionary<string, object> row, string column) =>
        TableValues.LongValue(row, column) is > 0 and var value ? value : null;

    private static IReadOnlyList<string> Models(string stored) =>
        stored.Length == 0 ? [] : [.. stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries)];

    private static CredentialReference? Credential(string environment, string file) => (environment, file) switch
    {
        ({ Length: > 0 }, _) => CredentialReference.ForEnvironment(environment),
        (_, { Length: > 0 }) => CredentialReference.ForFile(file),
        _ => null,
    };

    private static string? Optional(string value) => value.Length == 0 ? null : value;

    /// <summary>Derives the key a declaration's row is addressed by.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="name">The name it was applied under.</param>
    /// <returns>The key.</returns>
    private static string Key(string tenant, string name) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}\u001f{name}")))[..16];

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
                $"the table '{_tableName}' could not be created, so no provider config can be kept");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
