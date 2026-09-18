namespace Munarium.Core.Tests.Support;

using Munarium.Retrieval;

/// <summary>
/// An index-version store a test can hold in its hand, with the contract's own rules.
/// </summary>
/// <remarks>
/// It implements the seam's promises rather than a convenient approximation of them: a rebuild is idempotent, a
/// watermark advances forwards only, and a cutover leaves exactly one live version per collection.
/// </remarks>
/// <param name="time">The clock the lifecycle instants come from, so a test can pin them.</param>
internal sealed class InMemoryIndexVersionStore(TimeProvider? time = null) : IIndexVersionStore
{
    private readonly Lock _gate = new();

    // Identity keys are strings, and a Dictionary compares those ordinally by default - which is the comparison an
    // identity needs.
    private readonly Dictionary<string, IndexVersion> _versions = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Gets how many versions are recorded.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _versions.Count;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<IndexVersion> RegisterAsync(
        IndexVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_versions.TryGetValue(version.Id, out var existing))
            {
                _versions[version.Id] = version;

                return ValueTask.FromResult(version);
            }

            // A rebuild may advance the watermark and touch nothing else: a later build read a later ledger state,
            // and content that moved would make the identity meaningless.
            var advanced = version.Watermark.Value > existing.Watermark.Value
                ? existing with { Watermark = version.Watermark }
                : existing;

            _versions[version.Id] = advanced;

            return ValueTask.FromResult(advanced);
        }
    }

    /// <inheritdoc />
    public ValueTask<IndexVersion?> GetAsync(
        string tenant,
        string indexVersionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(
                _versions.GetValueOrDefault(indexVersionId) is { } found && string.Equals(found.Tenant, tenant, StringComparison.Ordinal)
                    ? found
                    : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IndexVersion?> ActiveAsync(
        string tenant,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(_versions.Values.FirstOrDefault(version =>
                version.Active &&
                string.Equals(version.Tenant, tenant, StringComparison.Ordinal) &&
                string.Equals(version.CollectionId, collectionId, StringComparison.Ordinal)));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IndexVersion>> ListActiveAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<IndexVersion> live =
            [
                .. _versions.Values
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
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<IndexVersion> versions =
            [
                .. _versions.Values
                    .Where(version => string.Equals(version.Tenant, tenant, StringComparison.Ordinal))
                    .Where(version => string.Equals(version.CollectionId, collectionId, StringComparison.Ordinal))
                    .OrderByDescending(version => version.Watermark.Value)
                    .ThenByDescending(version => version.Id, StringComparer.Ordinal),
            ];

            return ValueTask.FromResult(versions);
        }
    }

    /// <inheritdoc />
    public ValueTask<IndexVersion?> ActivateAsync(
        string tenant,
        string collectionId,
        string indexVersionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_versions.GetValueOrDefault(indexVersionId) is not { } target ||
                !string.Equals(target.Tenant, tenant, StringComparison.Ordinal) ||
                !string.Equals(target.CollectionId, collectionId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<IndexVersion?>(null);
            }

            foreach (var id in _versions.Keys.ToList())
            {
                var version = _versions[id];

                if (version.Active &&
                    string.Equals(version.Tenant, tenant, StringComparison.Ordinal) &&
                    string.Equals(version.CollectionId, collectionId, StringComparison.Ordinal))
                {
                    _versions[id] = version with { Active = false, DeactivatedAt = _time.GetUtcNow() };
                }
            }

            // Reactivating a superseded version keeps the instant it was first made live: when it started serving
            // is a fact about the version, not about this call.
            var activated = target with
            {
                Active = true,
                ActivatedAt = target.ActivatedAt ?? _time.GetUtcNow(),
                DeactivatedAt = null,
            };

            _versions[indexVersionId] = activated;

            return ValueTask.FromResult<IndexVersion?>(activated);
        }
    }
}
