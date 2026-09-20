namespace Munarium.Store.SharpCoreDb;

using Munarium.Access;
using SharpCoreDB.Interfaces;

/// <summary>
/// Issued capabilities and their withdrawals, held in a SharpCoreDB table.
/// </summary>
/// <remarks>
/// A table rather than a stream, for the same reason the source rows are: this is bookkeeping about credentials, not a fact
/// about the world that governance reasons over. Nothing here is a token - no token material is stored anywhere - so a row
/// is an identity and a set of claims, and it is the only lever a withdrawal has.
/// <para>
/// It is a table rather than a cache because a deny-list a restart forgets is not a deny-list: the day this answer lived in
/// a process, every withdrawal would be undone by the next deploy, and nothing would say so.
/// </para>
/// <para>
/// Predicates here are over the token id alone. That column is an identity the issuer mints, so it carries nothing that
/// needs quoting - while the tenant and the subject are caller-supplied text, which a predicate cannot compare (measured).
/// Those are compared after the read, on the one row a token id can name.
/// </para>
/// </remarks>
/// <param name="database">The database the rows live in.</param>
/// <param name="tableName">The table to hold them in.</param>
public sealed class SharpCoreDbAccessTokenAudit(
    IDatabase database,
    string tableName = SharpCoreDbAccessTokenAudit.DefaultTable) : IAccessTokenAudit
{
    /// <summary>The table a deployment gets when it does not choose one.</summary>
    public const string DefaultTable = "munarium_access_tokens";

    /// <summary>What joins a list in its column, matching the evidence plane's choice.</summary>
    private const string UnitSeparator = "\u001f";

    private const string Schema =
        "tenant TEXT, token_id TEXT, subject TEXT, level LONG, compartments TEXT, scopes TEXT, "
        + "runbooks TEXT, runbooks_scoped LONG, issued_at LONG, expires_at LONG, revoked_at LONG";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _tableName = TableValues.ValidateTableName(tableName);

    /// <inheritdoc />
    public ValueTask<IssuedCapability> RecordAsync(
        IssuedCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var table = Table();

            // A capability recorded again keeps any withdrawal it already has: re-recording is not a way to bring a
            // credential back, which is the one thing this table must never become.
            var recorded = Existing(table, capability.TokenId) is { RevokedAt: { } withdrawn }
                ? capability with { RevokedAt = withdrawn }
                : capability;

            Write(table, recorded);
            Persist();

            return ValueTask.FromResult(recorded);
        }
    }

    /// <inheritdoc />
    public ValueTask<IssuedCapability?> GetAsync(
        string tenant,
        string tokenId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var found = Existing(Table(), tokenId);

            return ValueTask.FromResult<IssuedCapability?>(
                found is not null && string.Equals(found.Tenant, tenant, StringComparison.Ordinal) ? found : null);
        }
    }

    /// <summary>Reads one row by its identity, without judging whose it is.</summary>
    /// <param name="table">The table to read from.</param>
    /// <param name="tokenId">The capability's identity.</param>
    /// <returns>The row, or <see langword="null"/> when there is none.</returns>
    private static IssuedCapability? Existing(ITable table, string tokenId)
    {
        var rows = table.Select(TableValues.Identity("token_id", tokenId)).ToList();

        return rows.Count == 0 ? null : Map(rows[0]);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IssuedCapability>> ListAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // Everything is read and the tenant is applied afterwards, because a caller-supplied tenant is a value and not
            // a predicate here. A tenant holds as many rows as it has live credentials, which is what makes that affordable.
            return ValueTask.FromResult<IReadOnlyList<IssuedCapability>>(
            [
                .. Table().Select()
                    .Select(Map)
                    .Where(row => string.Equals(row.Tenant, tenant, StringComparison.Ordinal))
                    .OrderByDescending(row => row.IssuedAt),
            ]);
        }
    }

    /// <inheritdoc />
    public ValueTask<IssuedCapability?> RevokeAsync(
        string tenant,
        string tokenId,
        long revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var table = Table();
            var found = Existing(table, tokenId);

            if (found is null || !string.Equals(found.Tenant, tenant, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<IssuedCapability?>(null);
            }

            // The first withdrawal is the one that stands and it is not moved by a second: an operator acting on a list
            // that was already stale has done nothing new, and the instant a credential stopped being accepted is the
            // fact worth keeping.
            if (found.RevokedAt is not null)
            {
                return ValueTask.FromResult<IssuedCapability?>(found);
            }

            var withdrawn = found with { RevokedAt = revokedAt };

            Write(table, withdrawn);
            Persist();

            return ValueTask.FromResult<IssuedCapability?>(withdrawn);
        }
    }

    /// <summary>Writes a row, replacing the one with the same identity.</summary>
    /// <param name="table">The table to write into.</param>
    /// <param name="row">The row to write.</param>
    private static void Write(ITable table, IssuedCapability row)
    {
        // An upsert rather than an append: a capability's identity is its token id, and a second row for one identity would
        // be two answers to whether it stands.
        table.Delete(TableValues.Identity("token_id", row.TokenId));
        table.Insert(new Dictionary<string, object>
        {
            ["tenant"] = row.Tenant,
            ["token_id"] = row.TokenId,
            ["subject"] = row.Subject,
            ["level"] = row.Level,
            ["compartments"] = string.Join(UnitSeparator, row.Compartments),
            ["scopes"] = string.Join(UnitSeparator, row.Scopes),

            // Two columns rather than one, because an absent runbook list means "any the level permits" while an empty one
            // means "none" - and a store that wrote both as empty would widen a capability from nothing to everything on
            // the way back out.
            ["runbooks"] = row.Runbooks is { } runbooks ? string.Join(UnitSeparator, runbooks) : string.Empty,
            ["runbooks_scoped"] = row.Runbooks is null ? 0L : 1L,
            ["issued_at"] = row.IssuedAt,
            ["expires_at"] = row.ExpiresAt,

            // Zero is "no withdrawal", which is unambiguous because a withdrawal is always after an issuance and an
            // issuance is never at the epoch.
            ["revoked_at"] = row.RevokedAt ?? 0L,
        });
    }

    /// <summary>Reads a row back.</summary>
    /// <param name="row">The stored columns.</param>
    /// <returns>The row.</returns>
    private static IssuedCapability Map(Dictionary<string, object> row) => new()
    {
        TokenId = TableValues.StringValue(row, "token_id"),
        Tenant = TableValues.StringValue(row, "tenant"),
        Subject = TableValues.StringValue(row, "subject"),
        Level = (int)TableValues.LongValue(row, "level"),
        Compartments = Texts(row, "compartments"),
        Scopes = Texts(row, "scopes"),
        Runbooks = TableValues.LongValue(row, "runbooks_scoped") == 0 ? null : Texts(row, "runbooks"),
        IssuedAt = TableValues.LongValue(row, "issued_at"),
        ExpiresAt = TableValues.LongValue(row, "expires_at"),
        RevokedAt = TableValues.LongValue(row, "revoked_at") is 0L ? null : TableValues.LongValue(row, "revoked_at"),
    };

    /// <summary>Reads a joined list column.</summary>
    /// <param name="row">The stored columns.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The values.</returns>
    private static IReadOnlyList<string> Texts(Dictionary<string, object> row, string column) =>
        TableValues.StringValue(row, column) is { Length: > 0 } joined
            ? joined.Split(UnitSeparator, StringSplitOptions.None)
            : [];

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
                $"the table '{_tableName}' could not be created, so no issuance can be recorded");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
