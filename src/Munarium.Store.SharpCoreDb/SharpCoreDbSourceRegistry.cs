namespace Munarium.Store.SharpCoreDb;

using System.Globalization;
using Munarium.Sources;
using SharpCoreDB.Interfaces;

/// <summary>
/// Source metadata rows, held in a SharpCoreDB table.
/// </summary>
/// <remarks>
/// A table rather than a stream, deliberately: a source row is retrieval bookkeeping and not ledger data. It is not
/// asserted by an actor, it carries no verdict, and nothing about the mesh's history depends on it - so putting it in
/// the ledger would make a document upload part of the record governance reasons over, which is a claim about the
/// document that nobody made.
/// <para>
/// This adapter stamps <see cref="SourceRecord.IngestedAt"/>, because a clock belongs to the server and not to the
/// kernel: an index identity or a verdict that had read one could not be rebuilt later. The stamp is the one field here
/// that is not a fact about the document.
/// </para>
/// <para>
/// Rows are written through the engine's table API and read back the same way, with the tenant and the path prefix
/// applied after the read. A caller-supplied path is a value and not a predicate: building a predicate out of it is how
/// a path with a quote in it stops matching itself, and a source that cannot be found by the path it was stored under is
/// a citation that leads nowhere.
/// </para>
/// </remarks>
/// <param name="database">The database the rows live in.</param>
/// <param name="tableName">The table to hold them in.</param>
public sealed class SharpCoreDbSourceRegistry(
    IDatabase database,
    string tableName = SharpCoreDbSourceRegistry.DefaultTable) : ISourceRegistry
{
    /// <summary>The table a deployment gets when it does not choose one.</summary>
    public const string DefaultTable = "munarium_sources";

    private const string Schema =
        "tenant TEXT, source_id TEXT, path TEXT, content_hash TEXT, media_type TEXT, "
        + "bytes_length LONG, blob_uri TEXT, backend_id TEXT, ingested_at TEXT";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _tableName = SourceTables.ValidateTableName(tableName);

    /// <inheritdoc />
    public ValueTask<SourceRecord> RecordAsync(
        SourceRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var stamped = record with
        {
            IngestedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        lock (_gate)
        {
            var table = Table();

            // A path is a source's identity, so this is an upsert rather than an append: re-ingesting a path is a new
            // version of one source, and a row left behind would be a source that exists twice.
            table.Delete(SourceTables.Identity("source_id", stamped.SourceId));
            table.Insert(new Dictionary<string, object>
            {
                ["tenant"] = stamped.Tenant,
                ["source_id"] = stamped.SourceId,
                ["path"] = stamped.Path,
                ["content_hash"] = stamped.ContentHash,
                ["media_type"] = stamped.MediaType,
                ["bytes_length"] = stamped.BytesLength,
                ["blob_uri"] = stamped.BlobUri,
                ["backend_id"] = stamped.BackendId,
                ["ingested_at"] = stamped.IngestedAt ?? string.Empty,
            });

            Persist();
        }

        return ValueTask.FromResult(stamped);
    }

    /// <inheritdoc />
    public ValueTask<SourceRecord?> FindAsync(
        string tenant,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);

        // The identity is derived from the tenant and the path, so a lookup by path is a lookup by identity - and the
        // row it names is checked against the request rather than trusted, because an identity is a hash and a hash can
        // in principle name two things.
        return MatchingAsync(
            SourceKey.Id(tenant, path),
            record => string.Equals(record.Tenant, tenant, StringComparison.Ordinal)
                && string.Equals(record.Path, path, StringComparison.Ordinal),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<SourceRecord?> GetAsync(
        string tenant,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);

        return MatchingAsync(
            sourceId,
            record => string.Equals(record.Tenant, tenant, StringComparison.Ordinal),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SourceRecord>> ListAsync(
        string tenant,
        string? pathPrefix = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<SourceRecord> records =
            [
                .. Table().Select()
                    .Select(Map)
                    .Where(record => string.Equals(record.Tenant, tenant, StringComparison.Ordinal))
                    .Where(record => pathPrefix is null
                        || record.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
                    .OrderBy(record => record.Path, StringComparer.Ordinal),
            ];

            return ValueTask.FromResult(records);
        }
    }

    private ValueTask<SourceRecord?> MatchingAsync(
        string sourceId,
        Func<SourceRecord, bool> matches,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var rows = Table().Select(SourceTables.Identity("source_id", sourceId));

            if (rows.Count == 0)
            {
                return ValueTask.FromResult<SourceRecord?>(null);
            }

            var record = Map(rows[0]);

            return ValueTask.FromResult<SourceRecord?>(matches(record) ? record : null);
        }
    }

    private static SourceRecord Map(Dictionary<string, object> row) => new()
    {
        Tenant = SourceTables.StringValue(row, "tenant"),
        SourceId = SourceTables.StringValue(row, "source_id"),
        Path = SourceTables.StringValue(row, "path"),
        ContentHash = SourceTables.StringValue(row, "content_hash"),
        MediaType = SourceTables.StringValue(row, "media_type"),
        BytesLength = SourceTables.LongValue(row, "bytes_length"),
        BlobUri = SourceTables.StringValue(row, "blob_uri"),
        BackendId = SourceTables.StringValue(row, "backend_id"),
        IngestedAt = SourceTables.StringValue(row, "ingested_at") is { Length: > 0 } stamp ? stamp : null,
    };

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
                $"the table '{_tableName}' could not be created, so no source can be recorded");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
