namespace Munarium.Store.SharpCoreDb;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Munarium.Authoring;
using SharpCoreDB.Interfaces;

/// <summary>Drafts, held in a SharpCoreDB table.</summary>
/// <remarks>
/// A table rather than anything cleverer: a draft is a name and a handful of answers, and all that has to survive is that
/// an author who comes back tomorrow finds what they wrote. The name is a caller's word, and this port measured that its
/// engine compares an identity and nothing else, so the row carries a digest of the name too and the name itself is
/// checked after the read - which is also what keeps two drafts whose names differ only in case two drafts.
/// <para>
/// The answers are stored as the text the kernel's codec writes, so a draft whose question set has grown still reads back:
/// an answer this build does not know is carried rather than dropped.
/// </para>
/// </remarks>
/// <param name="database">The database the drafts live in.</param>
/// <param name="tableName">The table to hold them in.</param>
public sealed class SharpCoreDbAuthoringDraftStore(
    IDatabase database,
    string tableName = SharpCoreDbAuthoringDraftStore.DefaultTable) : IAuthoringDraftStore
{
    /// <summary>The table a deployment gets when it does not choose one.</summary>
    public const string DefaultTable = "munarium_authoring_drafts";

    private const string Schema =
        "draft_key TEXT, name TEXT, pattern_id TEXT, answers TEXT, created_at TEXT, updated_at TEXT";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _tableName = TableValues.ValidateTableName(tableName);

    /// <inheritdoc />
    public ValueTask<AuthoringDraft> SaveAsync(AuthoringDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var table = Table();
            var stamped = draft with
            {
                // Creation is stamped once and a rewrite never moves it: when a draft was started is a fact about it, and
                // a store that reset it on every answer would make a draft look new every time it was touched.
                CreatedAt = Row(table, draft.Name)?.CreatedAt ?? now,
                UpdatedAt = now,
            };

            Write(table, stamped);
            Persist();

            return ValueTask.FromResult(stamped);
        }
    }

    /// <inheritdoc />
    public ValueTask<AuthoringDraft?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(Row(Table(), name));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AuthoringDraft>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<AuthoringDraft>>(
            [
                .. Table().Select()
                    .Select(Map)
                    .OrderByDescending(draft => draft.UpdatedAt, StringComparer.Ordinal),
            ]);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var table = Table();

            if (Row(table, name) is null)
            {
                return ValueTask.FromResult(false);
            }

            table.Delete(TableValues.Identity("draft_key", Key(name)));
            Persist();

            return ValueTask.FromResult(true);
        }
    }

    /// <summary>Reads one draft by its derived key, checking the name the row carries.</summary>
    /// <param name="table">The table to read from.</param>
    /// <param name="name">The name asked for.</param>
    /// <returns>The draft, or <see langword="null"/> when there is none or the row is another draft.</returns>
    private static AuthoringDraft? Row(ITable table, string name)
    {
        var rows = table.Select(TableValues.Identity("draft_key", Key(name))).ToList();

        if (rows.Count == 0)
        {
            return null;
        }

        var draft = Map(rows[0]);

        return string.Equals(draft.Name, name, StringComparison.Ordinal) ? draft : null;
    }

    /// <summary>Writes a row, replacing the one with the same derived key.</summary>
    /// <param name="table">The table to write into.</param>
    /// <param name="draft">The draft to write.</param>
    private static void Write(ITable table, AuthoringDraft draft)
    {
        table.Delete(TableValues.Identity("draft_key", Key(draft.Name)));
        table.Insert(new Dictionary<string, object>
        {
            ["draft_key"] = Key(draft.Name),
            ["name"] = draft.Name,
            ["pattern_id"] = draft.PatternId ?? string.Empty,
            ["answers"] = AuthoringAnswers.ToJson(draft.Answers),
            ["created_at"] = draft.CreatedAt ?? string.Empty,
            ["updated_at"] = draft.UpdatedAt ?? string.Empty,
        });
    }

    /// <summary>Reads a row back.</summary>
    /// <param name="row">The stored columns.</param>
    /// <returns>The draft.</returns>
    private static AuthoringDraft Map(Dictionary<string, object> row) => new()
    {
        Name = TableValues.StringValue(row, "name"),
        PatternId = TableValues.StringValue(row, "pattern_id") is { Length: > 0 } pattern ? pattern : null,
        Answers = AuthoringAnswers.FromJson(TableValues.StringValue(row, "answers")),
        CreatedAt = TableValues.StringValue(row, "created_at") is { Length: > 0 } created ? created : null,
        UpdatedAt = TableValues.StringValue(row, "updated_at") is { Length: > 0 } updated ? updated : null,
    };

    /// <summary>Derives the key a draft's row is addressed by.</summary>
    /// <param name="name">The draft's name.</param>
    /// <returns>The key.</returns>
    private static string Key(string name) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16];

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
                $"the table '{_tableName}' could not be created, so no draft can be kept");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
