namespace Munarium.Store.SharpCoreDb;

using Munarium.Sources;
using SharpCoreDB.Interfaces;

/// <summary>
/// Source bytes, held in a SharpCoreDB table.
/// </summary>
/// <remarks>
/// The bytes are base64 in a text column, which is how the event store holds an event payload - so the price is the one
/// that store already pays: a third more bytes, and a document that has to fit in memory to be written or read. That is
/// honest for the documents this port is built around, and the seam is what makes it a choice rather than a limit: an
/// object store or a filesystem implementation replaces this one without the kernel noticing.
/// <para>
/// The stored identity is the source id - a hash of tenant and path - while the path itself is carried alongside as
/// <c>blob_name</c> so an operator can see which document a blob is. That split is what makes a path with a quote in it
/// work: the value is written as a row rather than as SQL text, and the only predicate ever built is over the hash,
/// which contains nothing that needs quoting.
/// </para>
/// </remarks>
/// <param name="database">The database the bytes live in.</param>
/// <param name="tableName">The table to hold them in.</param>
public sealed class SharpCoreDbSourceStore(
    IDatabase database,
    string tableName = SharpCoreDbSourceStore.DefaultTable) : ISourceStore
{
    /// <summary>The table a deployment gets when it does not choose one.</summary>
    public const string DefaultTable = "munarium_source_blobs";

    private const string Schema =
        "blob_id TEXT, blob_name TEXT, media_type TEXT, content_base64 TEXT, bytes_length LONG";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly string _tableName = SourceTables.ValidateTableName(tableName);

    /// <inheritdoc />
    public string BackendId => "sharpcoredb";

    /// <inheritdoc />
    public ValueTask<string> PutAsync(
        SourceKey key,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(mediaType);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var table = Table();

            // Replace rather than append: one source holds one document's bytes, and a re-put is a new version of that
            // document rather than a second document.
            table.Delete(SourceTables.Identity("blob_id", key.SourceId));
            table.Insert(new Dictionary<string, object>
            {
                ["blob_id"] = key.SourceId,
                ["blob_name"] = key.BlobName,
                ["media_type"] = mediaType,
                ["content_base64"] = Convert.ToBase64String(bytes.Span),
                ["bytes_length"] = bytes.Length,
            });

            Persist();
        }

        return ValueTask.FromResult($"scdb://{key.BlobName}");
    }

    /// <inheritdoc />
    public ValueTask<byte[]> GetAsync(SourceKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var rows = Table().Select(SourceTables.Identity("blob_id", key.SourceId));

            // An absent blob reads as no bytes rather than as a failure: whether it exists is a different question,
            // which ExistsAsync answers.
            return ValueTask.FromResult(
                rows.Count == 0
                    ? []
                    : Convert.FromBase64String(SourceTables.StringValue(rows[0], "content_base64")));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(SourceKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(Table().Select(SourceTables.Identity("blob_id", key.SourceId)).Count > 0);
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(SourceKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Table().Delete(SourceTables.Identity("blob_id", key.SourceId));
            Persist();
        }

        return ValueTask.CompletedTask;
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
                $"the table '{_tableName}' could not be created, so no document can be stored");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
