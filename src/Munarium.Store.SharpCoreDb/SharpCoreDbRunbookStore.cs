namespace Munarium.Store.SharpCoreDb;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Munarium.Runbooks;
using SharpCoreDB.Interfaces;

/// <summary>
/// Applied runbook versions, held in a SharpCoreDB table.
/// </summary>
/// <remarks>
/// One row per version rather than per name: a session pins <c>name@version</c>, and an earlier version has to keep
/// answering after a newer one lands, because the turns that ran under it named it.
/// <para>
/// The YAML is stored verbatim next to the mapping it produced. The mapping is what runs and the bytes are what an
/// operator wrote, and the two answer different questions.
/// </para>
/// <para>
/// Rows go in and come out through the engine's table API and are filtered after the read, because a reference carries
/// caller-supplied text: a predicate over such a value matches nothing (measured), and this table is small enough that
/// reading it is cheaper than being wrong about which version is live.
/// </para>
/// </remarks>
/// <param name="database">The database the rows live in.</param>
/// <param name="tableName">The table to hold them in.</param>
public sealed class SharpCoreDbRunbookStore(
    IDatabase database,
    string tableName = SharpCoreDbRunbookStore.DefaultTable) : IRunbookStore
{
    /// <summary>The table a deployment gets when it does not choose one.</summary>
    public const string DefaultTable = "munarium_runbooks";

    private const string Schema =
        "row_id TEXT, tenant TEXT, runbook_ref TEXT, yaml TEXT, status TEXT, removal_id TEXT, "
        + "removal_requested_at TEXT, removal_requested_by TEXT, removed_at TEXT, created_at TEXT, updated_at TEXT";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _tableName = TableValues.ValidateTableName(tableName);

    /// <inheritdoc />
    public ValueTask<RunbookRecord> ApplyAsync(
        RunbookRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var existing = Stored(record.Tenant, record.Ref);

            // Re-applying resets any in-flight removal, because the bytes changed: a removal armed against the previous
            // content must not be able to remove the fresh version.
            var applied = record with
            {
                Status = RunbookStatus.Active,
                RemovalId = null,
                RemovalRequestedAt = null,
                RemovalRequestedBy = null,
                RemovedAt = null,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
            };

            var table = Table();

            table.Delete(TableValues.Identity("runbook_ref", applied.Ref));
            table.Insert(Row(applied));
            Persist();

            return ValueTask.FromResult(applied);
        }
    }

    /// <inheritdoc />
    public ValueTask<RunbookRecord?> GetAsync(
        string tenant,
        string runbookRef,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(runbookRef);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(Stored(tenant, runbookRef, includeRemoved));
        }
    }

    /// <inheritdoc />
    public ValueTask<RunbookRecord?> ResolveAsync(
        string tenant,
        string nameOrRef,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrRef);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // A reference resolves to exactly that version: a caller that named a version has already decided.
            if (nameOrRef.Contains('@', StringComparison.Ordinal))
            {
                return ValueTask.FromResult(Stored(tenant, nameOrRef, includeRemoved));
            }

            // A bare name resolves to the newest usable version, compared as a number: with versions in the double
            // digits, a string comparison would prefer 9 to 10.
            var newest = Visible(tenant, includeRemoved)
                .Where(record => string.Equals(record.Name, nameOrRef, StringComparison.Ordinal))
                .OrderByDescending(record => record.Version ?? 0)
                .ThenByDescending(record => record.Ref, StringComparer.Ordinal)
                .FirstOrDefault();

            return ValueTask.FromResult(newest);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RunbookRecord>> ListAsync(
        string tenant,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<RunbookRecord> records =
            [
                .. Visible(tenant, includeRemoved)
                    .OrderBy(record => record.Name, StringComparer.Ordinal)
                    .ThenBy(record => record.Version ?? 0),
            ];

            return ValueTask.FromResult(records);
        }
    }

    /// <inheritdoc />
    public ValueTask<RunbookRecord?> RequestRemovalAsync(
        string tenant,
        string runbookRef,
        string removalId,
        string at,
        string? by,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(runbookRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(removalId);
        ArgumentNullException.ThrowIfNull(at);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var stored = Stored(tenant, runbookRef, includeRemoved: true);

            return ValueTask.FromResult<RunbookRecord?>(
                stored is { Status: not RunbookStatus.Removed } record
                    ? Replace(record with
                    {
                        Status = RunbookStatus.RemoveRequested,
                        RemovalId = removalId,
                        RemovalRequestedAt = at,
                        RemovalRequestedBy = by,
                    })
                    : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<RunbookRecord?> ConfirmRemovalAsync(
        string tenant,
        string runbookRef,
        string removalId,
        string at,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(runbookRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(removalId);
        ArgumentNullException.ThrowIfNull(at);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var stored = Stored(tenant, runbookRef, includeRemoved: true);

            // Only the removal that armed the row may confirm it, and only while it is still armed: a version that was
            // re-applied in the meantime is a different document, and its status is active again.
            return ValueTask.FromResult<RunbookRecord?>(
                stored is { Status: RunbookStatus.RemoveRequested, RemovalId: { } armed } record
                && string.Equals(armed, removalId, StringComparison.Ordinal)
                    ? Replace(record with
                    {
                        Status = RunbookStatus.Removed,
                        RemovedAt = at,
                    })
                    : null);
        }
    }

    /// <summary>Writes a record back over its own row, moving its update stamp forward.</summary>
    /// <param name="record">The record to write.</param>
    /// <returns>The record as stored.</returns>
    private RunbookRecord Replace(RunbookRecord record)
    {
        var stamped = record with
        {
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        var table = Table();

        table.Delete(TableValues.Identity("row_id", RowId(stamped.Tenant, stamped.Ref)));
        table.Insert(Row(stamped));
        Persist();

        return stamped;
    }

    /// <summary>Every row this tenant holds, as records.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The records.</returns>
    private IEnumerable<RunbookRecord> Records(string tenant) =>
        Table()
            .Select()
            .Select(Record)
            .Where(record => string.Equals(record.Tenant, tenant, StringComparison.Ordinal));

    /// <summary>The rows a caller may see: everything but the removed ones, when those are hidden.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="includeRemoved">Whether removed versions are visible.</param>
    /// <returns>The visible records.</returns>
    private IEnumerable<RunbookRecord> Visible(string tenant, bool includeRemoved) =>
        Records(tenant).Where(record => includeRemoved || record.Status is not RunbookStatus.Removed);

    private RunbookRecord? Stored(string tenant, string runbookRef, bool includeRemoved = false) =>
        Visible(tenant, includeRemoved)
            .FirstOrDefault(record => string.Equals(record.Ref, runbookRef, StringComparison.Ordinal));

    /// <summary>
    /// The derived identity a row is addressed by.
    /// </summary>
    /// <remarks>
    /// A reference carries caller-supplied text, and a predicate over such a value matches nothing (measured), so the
    /// row is addressed by a hash of the pair the write has to be unambiguous about - the same shape this package's
    /// other tables address their rows by. The unit separator keeps a tenant or a name holding it from being able to
    /// reach another row's identity, which is the defect the evidence plane's domain key fixed.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="runbookRef">The reference.</param>
    /// <returns>The identity, as lowercase hex.</returns>
    private static string RowId(string tenant, string runbookRef) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}\u001f{runbookRef}")));

    private static RunbookRecord Record(Dictionary<string, object> row)
    {
        var runbookRef = TableValues.StringValue(row, "runbook_ref");

        return new RunbookRecord
        {
            Tenant = TableValues.StringValue(row, "tenant"),
            Ref = runbookRef,
            Yaml = TableValues.StringValue(row, "yaml"),
            Status = RunbookStatusNames.ParseStatus(TableValues.StringValue(row, "status"))
                ?? throw new FormatException($"runbook '{runbookRef}' is in a status this server does not know"),
            RemovalId = Moment(row, "removal_id"),
            RemovalRequestedAt = Moment(row, "removal_requested_at"),
            RemovalRequestedBy = Moment(row, "removal_requested_by"),
            RemovedAt = Moment(row, "removed_at"),
            CreatedAt = Moment(row, "created_at"),
            UpdatedAt = Moment(row, "updated_at"),
        };
    }

    private static Dictionary<string, object> Row(RunbookRecord record) => new()
    {
        ["row_id"] = RowId(record.Tenant, record.Ref),
        ["tenant"] = record.Tenant,
        ["runbook_ref"] = record.Ref,
        ["yaml"] = record.Yaml,
        ["status"] = record.Status.ToWireName(),
        ["removal_id"] = record.RemovalId ?? string.Empty,
        ["removal_requested_at"] = record.RemovalRequestedAt ?? string.Empty,
        ["removal_requested_by"] = record.RemovalRequestedBy ?? string.Empty,
        ["removed_at"] = record.RemovedAt ?? string.Empty,
        ["created_at"] = record.CreatedAt ?? string.Empty,
        ["updated_at"] = record.UpdatedAt ?? string.Empty,
    };

    private static string? Moment(Dictionary<string, object> row, string column) =>
        TableValues.StringValue(row, column) is { Length: > 0 } stamp ? stamp : null;

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
                $"the table '{_tableName}' could not be created, so no runbook can be applied");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
