namespace Munarium.Store.SharpCoreDb;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Munarium.Retrieval;
using SharpCoreDB.Interfaces;

/// <summary>An index version's chunks, held in a table of their own.</summary>
/// <remarks>
/// One table per index version, because a version's rows are written once and then read wholesale - so nothing ever has to
/// be filtered, and this port measured that its engine compares an identity and nothing else. A version id is text a
/// caller chose, so the table name carries a digest of it instead: an identity the engine can compare, and one a discard
/// drops by dropping the table rather than deleting rows a predicate at a time. The original partitions its chunk store by
/// collection for the same reason - a search should touch one version's rows and nothing else.
/// <para>
/// The embedding goes in the engine's own vector column. That is not decoration: it is what makes these rows the ones a
/// declared vector index could serve from, so the vector leg can move into the engine without the rows moving with it.
/// This port still fuses by rank itself, because its envelope records the ranking that decided the answer.
/// </para>
/// </remarks>
/// <param name="database">The database the chunks live in.</param>
/// <param name="tablePrefix">What every version's table is named under.</param>
public sealed class SharpCoreDbIndexChunkStore(
    IDatabase database,
    string tablePrefix = SharpCoreDbIndexChunkStore.DefaultPrefix) : IIndexChunkStore
{
    /// <summary>The prefix a deployment gets when it does not choose one.</summary>
    public const string DefaultPrefix = "munarium_chunks";

    private const string Schema =
        "chunk_id TEXT, source_id TEXT, source_path TEXT, content_hash TEXT, ordinal LONG, text TEXT, embedding VECTOR";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _prefix = TableValues.ValidateTableName(tablePrefix);

    /// <inheritdoc />
    public ValueTask<int> WriteAsync(
        string indexVersion,
        IReadOnlyList<PersistedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var table = Table(indexVersion);

            foreach (var chunk in chunks)
            {
                ArgumentNullException.ThrowIfNull(chunk);

                table.Insert(new Dictionary<string, object>
                {
                    ["chunk_id"] = chunk.Source.ChunkId,
                    ["source_id"] = chunk.Source.SourceId,
                    ["source_path"] = chunk.Source.SourcePath,
                    ["content_hash"] = chunk.Source.ContentHash,
                    ["ordinal"] = chunk.Source.ChunkOrdinal,
                    ["text"] = chunk.Text,
                    ["embedding"] = EmbeddingText(chunk.Embedding),
                });
            }

            if (chunks.Count > 0)
            {
                _database.Flush();
                _database.ForceSave();
            }

            return ValueTask.FromResult(chunks.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PersistedChunk>> ReadAsync(
        string indexVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_database.TryGetTable(Name(indexVersion), out var table))
            {
                return ValueTask.FromResult<IReadOnlyList<PersistedChunk>>([]);
            }

            // Everything is read and the order is applied afterwards: a version's rows are its own table, so there is
            // nothing to select between, and the ordinal is what makes two reads of one version agree.
            return ValueTask.FromResult<IReadOnlyList<PersistedChunk>>(
            [
                .. table.Select()
                    .Select(Map)
                    .OrderBy(chunk => chunk.Source.ChunkOrdinal),
            ]);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DropAsync(string indexVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var name = Name(indexVersion);

            if (!_database.TryGetTable(name, out _))
            {
                return ValueTask.FromResult(false);
            }

            _database.ExecuteSQL($"DROP TABLE {name}");

            return ValueTask.FromResult(true);
        }
    }

    /// <summary>Names the table one index version's chunks live in.</summary>
    /// <param name="indexVersion">The version.</param>
    /// <returns>The table name.</returns>
    private string Name(string indexVersion) =>
        string.Concat(
            _prefix,
            "_",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(indexVersion)))[..16]);

    /// <summary>Gets the table one version's chunks live in, creating it when it is not there yet.</summary>
    /// <param name="indexVersion">The version.</param>
    /// <returns>The table.</returns>
    private ITable Table(string indexVersion)
    {
        var name = Name(indexVersion);

        if (_database.TryGetTable(name, out var existing))
        {
            return existing;
        }

        _database.ExecuteSQL($"CREATE TABLE {name} ({Schema})");

        return _database.TryGetTable(name, out var created)
            ? created
            : throw new InvalidOperationException(
                $"the table '{name}' could not be created, so no chunk can be persisted");
    }

    /// <summary>Reads a chunk row back.</summary>
    /// <param name="row">The stored columns.</param>
    /// <returns>The chunk.</returns>
    private static PersistedChunk Map(Dictionary<string, object> row) => new()
    {
        Source = new SourceReference(
            TableValues.StringValue(row, "chunk_id"),
            TableValues.StringValue(row, "source_id"),
            TableValues.StringValue(row, "source_path"),
            TableValues.StringValue(row, "content_hash"),
            (int)TableValues.LongValue(row, "ordinal")),
        Text = TableValues.StringValue(row, "text"),
        Embedding = Vector(row),
    };

    /// <summary>Writes an embedding as the text the engine parses.</summary>
    /// <remarks>
    /// Measured: a VECTOR column accepts a float array on the way in but reads it back as the string System.Single, while
    /// the engine own parser takes text - a float array, bytes or JSON. So the round trip that works today is text in and
    /// text out. The array form is what the column documents, and this is the seam to remove when the engine closes it.
    /// </remarks>
    /// <param name="embedding">The embedding.</param>
    /// <returns>The text.</returns>
    private static string EmbeddingText(IReadOnlyList<float> embedding) =>
        string.Concat(
            "[",
            string.Join(",", embedding.Select(component => component.ToString("R", CultureInfo.InvariantCulture))),
            "]");

    /// <summary>Reads the embedding column, in whichever shape the engine hands it back.</summary>
    /// <remarks>
    /// The column is the engine's vector type, and what a read produces is the engine's business: its client maps that to a
    /// double array while the table API keeps floats, so both are accepted. Anything else is refused by name rather than
    /// by a cast that would fail somewhere with less to say.
    /// </remarks>
    /// <param name="row">The row.</param>
    /// <returns>The embedding.</returns>
    private static IReadOnlyList<float> Vector(Dictionary<string, object> row) =>
        row.TryGetValue("embedding", out var value)
            ? value switch
            {
                float[] single => single,
                double[] wide => [.. wide.Select(component => (float)component)],
                IReadOnlyList<float> floats => floats,
                IReadOnlyList<double> doubles => [.. doubles.Select(component => (float)component)],
                string text => Parse(text),
                _ => throw new InvalidOperationException(
                    $"the embedding column came back as {value?.GetType().Name ?? "null"}, which is not a vector"),
            }
            : [];

    /// <summary>Reads an embedding the engine handed back as text.</summary>
    /// <remarks>
    /// Measured: a vector column reads back as text through the table API, while the engine own client maps it to a double
    /// array. The two spellings the engine accepts on the way in are JSON and a bracketed list, so both are read here
    /// rather than only the one this store happens to write.
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <returns>The embedding.</returns>
    private static IReadOnlyList<float> Parse(string text)
    {
        var trimmed = text.Trim().Trim('[', ']');

        return trimmed.Length == 0
            ? []
            :
            [
                .. trimmed
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(component => float.Parse(component, CultureInfo.InvariantCulture)),
            ];
    }
}
