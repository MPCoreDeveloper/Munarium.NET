namespace Munarium.Store.SharpCoreDb;

using System.Globalization;
using Munarium.Ledger;
using Munarium.Retrieval;
using SharpCoreDB.Interfaces;

/// <summary>
/// Index versions, held in a SharpCoreDB table.
/// </summary>
/// <remarks>
/// A table rather than a stream, for the reason the source rows are: an index version is retrieval bookkeeping and not
/// ledger data. It is not asserted by an actor and it carries no verdict - no claim depends on which index answered a
/// question, and making it part of the record governance reasons over would put a serving decision into the history that
/// is supposed to be about what was claimed.
/// <para>
/// Two rules of the store's contract are enforced here rather than assumed. Registering an identity that is already
/// recorded returns what is recorded, because a version's content is fixed by its identity and a caller that arrives
/// with a different manifest under the same name cannot be allowed to rewrite history; only the watermark may move, and
/// only forwards, because a rebuild reads a later ledger state and can never un-read it. And a cutover deactivates
/// before it activates, so a crash leaves a collection with no live version - which an operator can see and fix - rather
/// than with two, which would be a silent wrong answer.
/// </para>
/// </remarks>
/// <param name="database">The database the versions live in.</param>
/// <param name="tableName">The table to hold them in.</param>
public sealed class SharpCoreDbIndexVersionStore(
    IDatabase database,
    string tableName = SharpCoreDbIndexVersionStore.DefaultTable) : IIndexVersionStore
{
    /// <summary>The table a deployment gets when it does not choose one.</summary>
    public const string DefaultTable = "munarium_index_versions";

    private const string Schema =
        "tenant TEXT, index_version_id TEXT, collection_id TEXT, shape_ref TEXT, watermark LONG, active LONG, "
        + "activated_at TEXT, deactivated_at TEXT, manifest TEXT, path_prefix TEXT";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _tableName = TableValues.ValidateTableName(tableName);

    /// <inheritdoc />
    public ValueTask<IndexVersion> RegisterAsync(
        IndexVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var table = Table();
            var existing = Read(table, version.Tenant, version.Id);

            if (existing is null)
            {
                Write(table, version);
                Persist();

                return ValueTask.FromResult(version);
            }

            // The same identity is the same version, so a second registration is not a rewrite: what is recorded stays,
            // and only a later watermark is carried forward.
            if (version.Watermark.Value <= existing.Watermark.Value)
            {
                return ValueTask.FromResult(existing);
            }

            var advanced = existing with { Watermark = version.Watermark };

            Write(table, advanced);
            Persist();

            return ValueTask.FromResult(advanced);
        }
    }

    /// <inheritdoc />
    public ValueTask<IndexVersion?> GetAsync(
        string tenant,
        string indexVersionId,
        CancellationToken cancellationToken = default) =>
        MatchingAsync(
            issuer: "index_version_id",
            tenant,
            indexVersionId,
            version => string.Equals(version.Tenant, tenant, StringComparison.Ordinal),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<IndexVersion?> ActiveAsync(
        string tenant,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(
                Table().Select()
                    .Select(Map)
                    .FirstOrDefault(version => version.Active
                        && string.Equals(version.Tenant, tenant, StringComparison.Ordinal)
                        && string.Equals(version.CollectionId, collectionId, StringComparison.Ordinal)));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IndexVersion>> ListActiveAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<IndexVersion> live =
            [
                .. Table().Select()
                    .Select(Map)
                    .Where(version => version.Active
                        && string.Equals(version.Tenant, tenant, StringComparison.Ordinal))
                    .OrderBy(version => version.CollectionId, StringComparer.Ordinal),
            ];

            return ValueTask.FromResult(live);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IndexVersion>> ListAsync(
        string tenant,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<IndexVersion> versions =
            [
                .. Table().Select()
                    .Select(Map)
                    .Where(version => string.Equals(version.Tenant, tenant, StringComparison.Ordinal))
                    .Where(version => string.Equals(version.CollectionId, collectionId, StringComparison.Ordinal))
                    .OrderByDescending(version => version.Watermark.Value)
                    .ThenByDescending(version => version.Id, StringComparer.Ordinal),
            ];

            return ValueTask.FromResult(versions);
        }
    }

    private ValueTask<IndexVersion?> MatchingAsync(
        string issuer,
        string tenant,
        string value,
        Func<IndexVersion, bool> matches,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var found = Table().Select(TableValues.Identity(issuer, value))
                .Select(Map)
                .FirstOrDefault(matches);

            return ValueTask.FromResult(found);
        }
    }

    /// <inheritdoc />
    public ValueTask<IndexVersion?> ActivateAsync(
        string tenant,
        string collectionId,
        string indexVersionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var table = Table();
            var target = Read(table, tenant, indexVersionId);

            if (target is null || !string.Equals(target.CollectionId, collectionId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<IndexVersion?>(null);
            }

            var now = DateTimeOffset.UtcNow;

            // The superseded version keeps the instant it stopped serving, so how long it was live stays answerable and
            // an envelope issued while it was live still resolves.
            foreach (var live in Live(table, tenant, collectionId)
                .Where(version => !string.Equals(version.Id, indexVersionId, StringComparison.Ordinal)))
            {
                Write(table, live with { Active = false, DeactivatedAt = now });
            }

            // Reactivating a superseded version keeps the instant it first went live: when it started serving is a fact
            // about the version, not about this call.
            var activated = target with
            {
                Active = true,
                ActivatedAt = target.ActivatedAt ?? now,
                DeactivatedAt = null,
            };

            Write(table, activated);
            Persist();

            return ValueTask.FromResult<IndexVersion?>(activated);
        }
    }

    private static IEnumerable<IndexVersion> Live(ITable table, string tenant, string collectionId) =>
        table.Select()
            .Select(Map)
            .Where(version => version.Active
                && string.Equals(version.Tenant, tenant, StringComparison.Ordinal)
                && string.Equals(version.CollectionId, collectionId, StringComparison.Ordinal));

    private static IndexVersion? Read(ITable table, string tenant, string indexVersionId) =>
        table.Select(TableValues.Identity("index_version_id", indexVersionId))
            .Select(Map)
            .FirstOrDefault(version => string.Equals(version.Tenant, tenant, StringComparison.Ordinal));

    private static void Write(ITable table, IndexVersion version)
    {
        table.Delete(TableValues.Identity("index_version_id", version.Id));
        table.Insert(new Dictionary<string, object>
        {
            ["tenant"] = version.Tenant,
            ["index_version_id"] = version.Id,
            ["collection_id"] = version.CollectionId,
            ["shape_ref"] = version.ShapeRef,
            ["watermark"] = version.Watermark.Value,
            ["active"] = version.Active ? 1L : 0L,
            ["activated_at"] = Stamp(version.ActivatedAt),
            ["deactivated_at"] = Stamp(version.DeactivatedAt),
            ["manifest"] = IndexManifestCodec.ToJson(version.Manifest),
            ["path_prefix"] = version.PathPrefix ?? string.Empty,
        });
    }

    private static IndexVersion Map(Dictionary<string, object> row) => new()
    {
        Id = TableValues.StringValue(row, "index_version_id"),
        Tenant = TableValues.StringValue(row, "tenant"),
        CollectionId = TableValues.StringValue(row, "collection_id"),
        ShapeRef = TableValues.StringValue(row, "shape_ref"),
        Watermark = new SequenceNumber(TableValues.LongValue(row, "watermark")),
        Active = TableValues.LongValue(row, "active") != 0,
        ActivatedAt = Moment(TableValues.StringValue(row, "activated_at")),
        DeactivatedAt = Moment(TableValues.StringValue(row, "deactivated_at")),
        Manifest = IndexManifestCodec.FromJson(
            TableValues.StringValue(row, "manifest"),
            "stored index manifest"),
        PathPrefix = TableValues.StringValue(row, "path_prefix") is { Length: > 0 } prefix ? prefix : null,
    };

    private static string Stamp(DateTimeOffset? moment) =>
        moment?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    private static DateTimeOffset? Moment(string stamp) =>
        DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

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
                $"the table '{_tableName}' could not be created, so no index version can be recorded");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
