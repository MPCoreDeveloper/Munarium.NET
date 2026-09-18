namespace Munarium.Retrieval;

using Munarium.Ledger;

/// <summary>
/// A request to record an index version.
/// </summary>
/// <remarks>
/// The caller supplies what the version was built from; the identity and the cutover are the catalogue's, because
/// an identity a caller could choose would let two corpora share a name.
/// </remarks>
public sealed record IndexBuildRequest
{
    /// <summary>Gets the tenant.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets what the version was built from.</summary>
    public required IndexManifest Manifest { get; init; }

    /// <summary>Gets the sources bound into the version, which the identity hashes.</summary>
    public required IReadOnlyList<IndexedSource> Sources { get; init; }

    /// <summary>Gets the ledger position the build reflects.</summary>
    public required SequenceNumber Watermark { get; init; }

    /// <summary>
    /// Gets a value indicating whether the built version should become the collection's live one.
    /// </summary>
    /// <remarks>
    /// Defaulted to off so that building and serving are separate decisions: a build nobody has inspected is not a
    /// build that should be answering questions.
    /// </remarks>
    public bool Activate { get; init; }
}

/// <summary>
/// What an envelope resolves to, and whether the version it names agrees with it.
/// </summary>
public sealed record EnvelopeResolution
{
    /// <summary>Gets the envelope that was resolved.</summary>
    public required ProvenanceEnvelope Envelope { get; init; }

    /// <summary>Gets the version the envelope names, or <see langword="null"/> when there is none.</summary>
    public IndexVersion? Version { get; init; }

    /// <summary>
    /// Gets the content hashes the answer cites that the version's manifest does not record.
    /// </summary>
    /// <remarks>
    /// A version records the bytes it indexed, so an answer citing bytes outside that set cannot have come from it.
    /// This is the part of provenance that a mixed-up envelope cannot fake.
    /// </remarks>
    public required IReadOnlyList<string> UnrecordedContentHashes { get; init; }

    /// <summary>Gets a value indicating whether the answer's provenance holds up.</summary>
    public bool Resolved =>
        Version is not null &&
        UnrecordedContentHashes.Count == 0 &&
        !FrontRunsVersion;

    /// <summary>Gets why the answer's provenance does not hold up, or <see langword="null"/> when it does.</summary>
    public string? Failure => (Version is null, UnrecordedContentHashes.Count > 0, FrontRunsVersion) switch
    {
        (true, _, _) => $"index version '{Envelope.IndexVersion}' cannot be resolved",
        (_, true, _) => "the answer cites bytes this index version never held",
        (_, _, true) => "the answer claims a ledger position this index version never reflected",
        _ => null,
    };

    // The envelope's watermark may be older than the version's - it was answered before a rebuild advanced it - but
    // never newer: an answer cannot have reflected more of the ledger than the index it was answered from.
    private bool FrontRunsVersion =>
        Version is not null && Envelope.LedgerWatermark.Value > Version.Watermark.Value;
}

/// <summary>
/// The catalogue: the rules an index version's life runs under.
/// </summary>
/// <remarks>
/// Three rules, and each exists to make an answer's provenance checkable later:
/// <list type="number">
/// <item>an identity is derived, never chosen, so two corpora cannot share one;</item>
/// <item>a build records a version without making it live, so serving is a decision and not a side effect of
/// building;</item>
/// <item>a cutover is per collection and atomic, so exactly one version answers a new query and the superseded one
/// stays resolvable for old ones.</item>
/// </list>
/// </remarks>
/// <param name="store">The persistence.</param>
public sealed class IndexCatalog(IIndexVersionStore store)
{
    private readonly IIndexVersionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Records a build, and cuts the collection over to it when the request says so.
    /// </summary>
    /// <param name="request">What was built.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recorded version, live when the request asked for it.</returns>
    /// <exception cref="ArgumentException">Thrown when the build names no sources.</exception>
    public async ValueTask<IndexVersion> RegisterAsync(
        IndexBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Sources.Count == 0)
        {
            throw new ArgumentException(
                "An index version indexes at least one source; ingest or bind sources before building.",
                nameof(request));
        }

        var manifest = request.Manifest;
        var id = IndexVersionIds.Of(
            manifest.CollectionId,
            manifest.ShapeRef,
            manifest.Chunker,
            manifest.Extractors,
            manifest.Embedder,
            request.Sources);

        var recorded = await _store.RegisterAsync(
            new IndexVersion
            {
                Id = id,
                Tenant = request.Tenant,
                CollectionId = manifest.CollectionId,
                ShapeRef = manifest.ShapeRef,
                Manifest = manifest,
                Watermark = request.Watermark,
            },
            cancellationToken).ConfigureAwait(false);

        var activated = request.Activate
            ? await _store.ActivateAsync(request.Tenant, manifest.CollectionId, recorded.Id, cancellationToken)
                .ConfigureAwait(false)
            : null;

        return activated ?? recorded;
    }

    /// <summary>Cuts a collection over to a version.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="collectionId">The collection.</param>
    /// <param name="indexVersionId">The version to make live.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activated version, or <see langword="null"/> when the collection has no such version.</returns>
    public ValueTask<IndexVersion?> ActivateAsync(
        string tenant,
        string collectionId,
        string indexVersionId,
        CancellationToken cancellationToken = default) =>
        _store.ActivateAsync(tenant, collectionId, indexVersionId, cancellationToken);

    /// <summary>Reads a collection's live version.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="collectionId">The collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active version, or <see langword="null"/> when the collection has none.</returns>
    public ValueTask<IndexVersion?> ActiveAsync(
        string tenant,
        string collectionId,
        CancellationToken cancellationToken = default) =>
        _store.ActiveAsync(tenant, collectionId, cancellationToken);

    /// <summary>
    /// Resolves an answer's envelope back to its index version, and checks that the two agree.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="envelope">The envelope the answer carried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the envelope names, and whether the answer's provenance holds up.</returns>
    public async ValueTask<EnvelopeResolution> ResolveAsync(
        string tenant,
        ProvenanceEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var version = await _store.GetAsync(tenant, envelope.IndexVersion, cancellationToken).ConfigureAwait(false);

        var unrecorded = version is null
            ? []
            : envelope
                .Sources
                .Select(source => source.ContentHash)
                .Distinct(StringComparer.Ordinal)
                .Where(hash => !version.Manifest.SourceContentHashes.Contains(hash, StringComparer.Ordinal))
                .ToList();

        return new EnvelopeResolution
        {
            Envelope = envelope,
            Version = version,
            UnrecordedContentHashes = unrecorded,
        };
    }
}
