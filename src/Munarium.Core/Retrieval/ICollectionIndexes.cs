namespace Munarium.Retrieval;

/// <summary>
/// Answers which reader serves a collection, right now.
/// </summary>
/// <remarks>
/// The seam between a runbook's vocabulary and an index instance. A runbook names collections; a deployment holds index
/// versions, one live per collection, and instances of them beside it. Which version is live is the catalogue's answer
/// and which reader answers for that version is the host's, so both are read at the moment of use rather than held by a
/// caller: a cutover must not move the ground under a turn that has already been told which collections it may read.
/// </remarks>
public interface ICollectionIndexes
{
    /// <summary>Gets the reader that answers for a collection.</summary>
    /// <param name="collection">The collection's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The reader, or <see langword="null"/> when the collection has no live version whose chunks are in this process -
    /// which is a collection a turn may read and cannot search, not an error.
    /// </returns>
    ValueTask<IRetrievalBackend?> ReaderForAsync(string collection, CancellationToken cancellationToken = default);
}

/// <summary>
/// The readers a deployment has, resolved through the catalogue's live version per collection and the host's instances.
/// </summary>
/// <remarks>
/// The collection is found by the name its manifest records, and that is the whole reason the manifest records it: an
/// operator reading a manifest needs no second lookup, and neither does a turn. Resolving a name through the live
/// versions rather than through a registry of its own means there is one answer to "which version serves this
/// collection", and it is the same answer the cutover route writes.
/// </remarks>
/// <param name="versions">The catalogue of index versions.</param>
/// <param name="indexes">The instances this process holds.</param>
/// <param name="tenant">The deployment's tenant.</param>
public sealed class CollectionIndexes(IIndexVersionStore versions, IIndexHost indexes, string tenant) : ICollectionIndexes
{
    private readonly IIndexVersionStore _versions = versions ?? throw new ArgumentNullException(nameof(versions));
    private readonly IIndexHost _indexes = indexes ?? throw new ArgumentNullException(nameof(indexes));
    private readonly string _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));

    /// <summary>Gets the collections that have a live version here, by name.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The collection names, ordered, so a caller's own order is applied to a stable list.</returns>
    public async ValueTask<IReadOnlyList<string>> LiveCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var live = await _versions.ListActiveAsync(_tenant, cancellationToken).ConfigureAwait(false);

        return
        [
            .. live
                .Select(version => version.Manifest.CollectionName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<IRetrievalBackend?> ReaderForAsync(
        string collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);

        var live = await _versions.ListActiveAsync(_tenant, cancellationToken).ConfigureAwait(false);

        var version = live.FirstOrDefault(candidate =>
            string.Equals(candidate.Manifest.CollectionName, collection, StringComparison.Ordinal));

        return version is null ? null : _indexes.ReaderFor(version.Id);
    }
}
