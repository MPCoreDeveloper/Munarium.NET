namespace Munarium.Core.Tests.Support;

using Munarium.Sources;

/// <summary>
/// An in-memory <see cref="ISourceRegistry"/> for kernel tests.
/// </summary>
/// <remarks>
/// Keyed by tenant and path - ordinally, the comparison a path needs - so a test can prove that a re-ingest of one
/// path replaces one row rather than adding a second, and that two tenants with the same path stay separate. It also
/// stamps <see cref="SourceRecord.IngestedAt"/>, because that is the backend's job and a double that skipped it would
/// hide a row that never gets a stamp.
/// </remarks>
internal sealed class InMemorySourceRegistry : ISourceRegistry
{
    private readonly Dictionary<string, SourceRecord> _rows = [];
    private int _stamps;

    /// <summary>Gets how many rows are held.</summary>
    public int Count => _rows.Count;

    /// <inheritdoc />
    public ValueTask<SourceRecord> RecordAsync(
        SourceRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var stamped = record with { IngestedAt = $"stamp-{++_stamps}" };

        _rows[Key(record.Tenant, record.Path)] = stamped;

        return ValueTask.FromResult(stamped);
    }

    /// <inheritdoc />
    public ValueTask<SourceRecord?> FindAsync(
        string tenant,
        string path,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_rows.GetValueOrDefault(Key(tenant, path)));

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SourceRecord>> ListAsync(
        string tenant,
        string? pathPrefix = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SourceRecord> rows =
        [
            .. _rows.Values
                .Where(row => string.Equals(row.Tenant, tenant, StringComparison.Ordinal))
                .Where(row => pathPrefix is null || row.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
                .OrderBy(row => row.Path, StringComparer.Ordinal),
        ];

        return ValueTask.FromResult(rows);
    }

    private static string Key(string tenant, string path) => string.Concat(tenant, "/", path);
}
