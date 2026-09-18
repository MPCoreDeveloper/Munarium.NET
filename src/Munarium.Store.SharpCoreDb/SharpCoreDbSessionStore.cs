namespace Munarium.Store.SharpCoreDb;

using System.Globalization;
using Munarium.Access;
using Munarium.Sessions;
using SharpCoreDB.Interfaces;

/// <summary>
/// Sessions and their turns, held in two SharpCoreDB tables.
/// </summary>
/// <remarks>
/// Tables of their own rather than ledger data, for the same reason source rows are: a conversation is not a claim about
/// the world, and asking a question is not a verdict anybody asserted. Putting turns in the ledger would make a
/// transcript part of the record governance reasons over.
/// <para>
/// The adapter stamps both timestamps, because a clock read inside the kernel would make anything derived from it
/// unreproducible, and neither stamp decides anything - they are read by an operator asking what happened.
/// </para>
/// <para>
/// Rows are written and read through the engine's table API, with the tenant and the uid compared after the read: a
/// predicate over caller-supplied text matches nothing (measured), while the identities these tables are looked up by -
/// session ids that are ULIDs - are safe to build one over.
/// </para>
/// </remarks>
/// <param name="database">The database the rows live in.</param>
/// <param name="sessionsTable">The table to hold sessions in.</param>
/// <param name="turnsTable">The table to hold turns in.</param>
public sealed class SharpCoreDbSessionStore(
    IDatabase database,
    string sessionsTable = SharpCoreDbSessionStore.DefaultSessionsTable,
    string turnsTable = SharpCoreDbSessionStore.DefaultTurnsTable) : ISessionStore
{
    /// <summary>The sessions table a deployment gets when it does not choose one.</summary>
    public const string DefaultSessionsTable = "munarium_sessions";

    /// <summary>The turns table a deployment gets when it does not choose one.</summary>
    public const string DefaultTurnsTable = "munarium_session_turns";

    private const string UnitSeparator = "\u001f";

    private const string SessionsSchema =
        "tenant TEXT, id TEXT, uid TEXT, runbook_ref TEXT, token_jti TEXT, access_level LONG, "
        + "compartments TEXT, all_compartments LONG, runbooks TEXT, state TEXT, created_at TEXT, last_turn_at TEXT";

    private const string TurnsSchema =
        "tenant TEXT, session_id TEXT, ordinal LONG, uid TEXT, query TEXT, collections TEXT, hits TEXT, "
        + "envelope TEXT, completion TEXT, hierarchy TEXT, created_at TEXT";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _sessionsTable = TableValues.ValidateTableName(sessionsTable);
    private readonly string _turnsTable = TableValues.ValidateTableName(turnsTable);

    /// <inheritdoc />
    public ValueTask<SessionRecord> CreateAsync(
        SessionRecord session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // The session's clearance is the one taken at creation, so an existing row stands: re-storing a session must
            // not be a way for a later call to widen or narrow what an ongoing conversation can see.
            if (Existing(session.Tenant, session.Id) is { } stored)
            {
                return ValueTask.FromResult(stored);
            }

            var stamped = session with
            {
                CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };

            var table = Table((_sessionsTable, SessionsSchema));
            table.Delete(TableValues.Identity("id", stamped.Id));
            table.Insert(SessionRow(stamped));
            Persist();

            return ValueTask.FromResult(stamped);
        }
    }

    /// <inheritdoc />
    public ValueTask<SessionRecord?> GetAsync(
        string tenant,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(Existing(tenant, sessionId));
        }
    }

    /// <inheritdoc />
    public ValueTask<int> AppendTurnAsync(
        TurnRecord turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var ordinal = NextOrdinal(turn.SessionId);
            var stamped = turn with
            {
                Ordinal = ordinal,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };

            Table((_turnsTable, TurnsSchema)).Insert(TurnRow(stamped));
            StampLastTurn(stamped);

            Persist();

            return ValueTask.FromResult(ordinal);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> CloseAsync(
        string tenant,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (Existing(tenant, sessionId) is not { State: SessionState.Open } open)
            {
                return ValueTask.FromResult(false);
            }

            var closed = open with { State = SessionState.Closed };
            var table = Table((_sessionsTable, SessionsSchema));

            table.Delete(TableValues.Identity("id", closed.Id));
            table.Insert(SessionRow(closed));
            Persist();

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TurnRecord>> TurnsAsync(
        string tenant,
        string sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<TurnRecord> turns =
            [
                .. Table((_turnsTable, TurnsSchema))
                    .Select(TableValues.Identity("session_id", sessionId))
                    .Select(Turn)
                    .Where(turn => string.Equals(turn.Tenant, tenant, StringComparison.Ordinal))
                    .OrderBy(turn => turn.Ordinal)
                    .Take(limit),
            ];

            return ValueTask.FromResult(turns);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SessionRecord>> RecentAsync(
        string tenant,
        string uid,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<SessionRecord> sessions =
            [
                .. Table((_sessionsTable, SessionsSchema))
                    .Select()
                    .Select(Session)
                    .Where(session => string.Equals(session.Tenant, tenant, StringComparison.Ordinal))
                    .Where(session => string.Equals(session.Uid, uid, StringComparison.Ordinal))
                    .OrderByDescending(session => session.CreatedAt, StringComparer.Ordinal)
                    .Take(limit),
            ];

            return ValueTask.FromResult(sessions);
        }
    }

    private static string Joined(IReadOnlyList<string> values) => string.Join(UnitSeparator, values);

    private static IReadOnlyList<string> Split(string text) =>
        text is { Length: > 0 } ? text.Split(UnitSeparator, StringSplitOptions.None) : [];

    private static string? Moment(Dictionary<string, object> row, string column) =>
        TableValues.StringValue(row, column) is { Length: > 0 } stamp ? stamp : null;

    private static SessionRecord Session(Dictionary<string, object> row)
    {
        var id = TableValues.StringValue(row, "id");
        var names = Split(TableValues.StringValue(row, "runbooks"));

        return new SessionRecord
        {
            Tenant = TableValues.StringValue(row, "tenant"),
            Id = id,
            Uid = TableValues.StringValue(row, "uid"),
            RunbookRef = TableValues.StringValue(row, "runbook_ref"),
            TokenJti = Moment(row, "token_jti"),
            Access = new AccessContext(
                (int)TableValues.LongValue(row, "access_level"),
                Split(TableValues.StringValue(row, "compartments")),
                TableValues.LongValue(row, "all_compartments") != 0,
                names.Count > 0 ? names : null),
            State = SessionStateNames.ParseState(TableValues.StringValue(row, "state"))
                ?? throw new FormatException($"session '{id}' is in a state this server does not know"),
            CreatedAt = Moment(row, "created_at"),
            LastTurnAt = Moment(row, "last_turn_at"),
        };
    }

    private static TurnRecord Turn(Dictionary<string, object> row) => new()
    {
        Tenant = TableValues.StringValue(row, "tenant"),
        SessionId = TableValues.StringValue(row, "session_id"),
        Ordinal = (int)TableValues.LongValue(row, "ordinal"),
        Uid = TableValues.StringValue(row, "uid"),
        Query = TableValues.StringValue(row, "query"),
        CollectionsSearched = Split(TableValues.StringValue(row, "collections")),
        HitsJson = TableValues.StringValue(row, "hits"),
        EnvelopeJson = TableValues.StringValue(row, "envelope"),
        CompletionJson = Moment(row, "completion"),
        HierarchyJson = Moment(row, "hierarchy"),
        CreatedAt = Moment(row, "created_at"),
    };

    private SessionRecord? Existing(string tenant, string sessionId) =>
        Table((_sessionsTable, SessionsSchema))
            .Select(TableValues.Identity("id", sessionId))
            .Select(Session)
            .FirstOrDefault(session => string.Equals(session.Tenant, tenant, StringComparison.Ordinal));

    /// <summary>
    /// Allocates the next ordinal for a session's turns.
    /// </summary>
    /// <remarks>
    /// The original allocates this inside the insert - a <c>SELECT COALESCE(MAX(ordinal), 0) + 1</c> in the values
    /// clause - and retries three times when the key it lands on was taken, because two concurrent turns can compute the
    /// same maximum. This allocates under the adapter's lock instead, which is the same guarantee inside one process and
    /// is what this engine allows: a declared primary key is a way to lose rows here (measured), so there is no key for a
    /// second writer to collide on and no violation to retry. A deployment that gave one file two writers would have to
    /// revisit this rather than inherit it silently.
    /// </remarks>
    /// <param name="sessionId">The session.</param>
    /// <returns>The ordinal the next turn should take, one-based.</returns>
    private int NextOrdinal(string sessionId) =>
        (int)(Table((_turnsTable, TurnsSchema))
            .Select(TableValues.Identity("session_id", sessionId))
            .Select(row => TableValues.LongValue(row, "ordinal"))
            .DefaultIfEmpty(0L)
            .Max()
            + 1L);

    /// <summary>Moves a session's last-turn stamp forward, when the session is one this store holds.</summary>
    /// <param name="turn">The turn that was just recorded.</param>
    private void StampLastTurn(TurnRecord turn)
    {
        if (Existing(turn.Tenant, turn.SessionId) is not { } session)
        {
            return;
        }

        var table = Table((_sessionsTable, SessionsSchema));

        table.Delete(TableValues.Identity("id", session.Id));
        table.Insert(SessionRow(session with { LastTurnAt = turn.CreatedAt }));
    }

    private static Dictionary<string, object> SessionRow(SessionRecord session) => new()
    {
        ["tenant"] = session.Tenant,
        ["id"] = session.Id,
        ["uid"] = session.Uid,
        ["runbook_ref"] = session.RunbookRef,
        ["token_jti"] = session.TokenJti ?? string.Empty,
        ["access_level"] = (long)session.Access.Level,
        ["compartments"] = Joined(session.Access.Compartments),
        ["all_compartments"] = session.Access.AllCompartments ? 1L : 0L,
        ["runbooks"] = Joined(session.Access.Runbooks ?? []),
        ["state"] = session.State.ToWireName(),
        ["created_at"] = session.CreatedAt ?? string.Empty,
        ["last_turn_at"] = session.LastTurnAt ?? string.Empty,
    };

    private static Dictionary<string, object> TurnRow(TurnRecord turn) => new()
    {
        ["tenant"] = turn.Tenant,
        ["session_id"] = turn.SessionId,
        ["ordinal"] = (long)turn.Ordinal,
        ["uid"] = turn.Uid,
        ["query"] = turn.Query,
        ["collections"] = Joined(turn.CollectionsSearched),
        ["hits"] = turn.HitsJson,
        ["envelope"] = turn.EnvelopeJson,
        ["completion"] = turn.CompletionJson ?? string.Empty,
        ["hierarchy"] = turn.HierarchyJson ?? string.Empty,
        ["created_at"] = turn.CreatedAt ?? string.Empty,
    };

    private ITable Table((string Name, string Schema) table)
    {
        if (_database.TryGetTable(table.Name, out var existing))
        {
            return existing;
        }

        _database.ExecuteSQL($"CREATE TABLE {table.Name} ({table.Schema})");

        return _database.TryGetTable(table.Name, out var created)
            ? created
            : throw new InvalidOperationException(
                $"the table '{table.Name}' could not be created, so no conversation can be kept");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
