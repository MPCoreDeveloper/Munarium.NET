namespace Munarium.Store.SharpCoreDb;

using System.Globalization;
using Munarium.Idempotency;
using SharpCoreDB.Interfaces;

/// <summary>
/// Idempotency keys and the answers they stand for, held in a SharpCoreDB table.
/// </summary>
/// <remarks>
/// A table of its own rather than a corner of the ledger: a key is not a fact about the world, it is a fact about a
/// conversation - which command a caller sent twice - and the ledger is the record governance reasons over. Keeping it
/// here also keeps the payload out of the ledger's history, where a replay of an answer would look like a second answer.
/// <para>
/// The scope is part of the stored identity, so the same key on another operation is another request; and a record is
/// the first one, because two answers to one request is the situation the whole seam exists to prevent.
/// </para>
/// </remarks>
/// <param name="database">The database the keys live in.</param>
/// <param name="tableName">The table to hold them in.</param>
public sealed class SharpCoreDbIdempotencyStore(
    IDatabase database,
    string tableName = SharpCoreDbIdempotencyStore.DefaultTable) : IIdempotencyStore
{
    /// <summary>The table a deployment gets when it does not choose one.</summary>
    public const string DefaultTable = "munarium_idempotency";

    private const string Schema =
        "tenant TEXT, scope TEXT, key TEXT, payload TEXT, recorded_at TEXT";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _tableName = TableValues.ValidateTableName(tableName);

    /// <inheritdoc />
    public ValueTask<string?> FindAsync(
        string tenant,
        string scope,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // The predicate is on the key alone, because a key is a ULID and carries nothing that needs quoting, while
            // the tenant and the scope are caller-supplied text - which a predicate cannot compare (measured). Those two
            // are compared after the read, on the few rows a key can name.
            var rows = Table()
                .Select(TableValues.Identity("key", key))
                .Where(row => string.Equals(TableValues.StringValue(row, "tenant"), tenant, StringComparison.Ordinal))
                .Where(row => string.Equals(TableValues.StringValue(row, "scope"), scope, StringComparison.Ordinal))
                .ToList();

            return ValueTask.FromResult(
                rows.Count == 0 ? null : TableValues.StringValue(rows[0], "payload"));
        }
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(
        string tenant,
        string scope,
        string key,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        // The first answer is the answer: a second record under the same key is ignored, not applied, because the caller
        // that retried has already been told what the first one said.
        if (await FindAsync(tenant, scope, key, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        lock (_gate)
        {
            var table = Table();

            table.Insert(new Dictionary<string, object>
            {
                ["tenant"] = tenant,
                ["scope"] = scope,
                ["key"] = key,
                ["payload"] = payload,
                ["recorded_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });

            Persist();
        }
    }

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
                $"the table '{_tableName}' could not be created, so no command can be keyed");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
