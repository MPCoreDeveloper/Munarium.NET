namespace Munarium.Evidence;

using Munarium.Claims;
using Munarium.Facts;

/// <summary>
/// A pinned slice of the ledger's own accepted facts.
/// </summary>
/// <remarks>
/// A turn reads the memory version its profile pinned, rather than whatever the head happens to be, so two turns in one
/// session cannot silently disagree because an ingest landed between them.
/// <para>
/// [gap] A session cannot yet pin its own version - sessions carry no version binding - so the pin is per profile and
/// not per conversation. The original records the same gap in the same words, and it is repeated here rather than
/// smoothed over: this is a property of the system, not of the port.
/// </para>
/// <para>
/// One behaviour differs from the original, and it comes from this port's ledger read rather than from a choice made
/// here. The original's store raised not-found for a version that is not there, and the provider refused with
/// <c>source-unavailable</c>. This port's read model has no such branch: a version nobody wrote simply holds no facts,
/// so an unknown version reads as a layer that produced nothing rather than as one that could not be read. Everything
/// else - including the refusal when a layer names no version at all - is the original's.
/// </para>
/// </remarks>
/// <param name="ledger">The fact read model over the ledger.</param>
public sealed class FactProvider(FactLedger ledger) : IEvidenceProvider
{
    /// <summary>
    /// The prefix a layer pins a scope prefix with.
    /// </summary>
    /// <remarks>
    /// Only the fact plane reads it: a document collection and a data view are addressed by name, while the ledger's
    /// facts live in a scope tree and a layer may want one branch of it.
    /// </remarks>
    private const string ScopePrefix = "scope:";

    private readonly FactLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <inheritdoc />
    public string Id => EvidencePlanes.Facts;

    /// <inheritdoc />
    public bool CanServe(string source) =>
        source.StartsWith(EvidencePlanes.FactsPrefix, StringComparison.Ordinal);

    /// <inheritdoc />
    public async ValueTask<EvidenceBlock> FetchAsync(
        EvidenceLayer layer,
        QueryIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layer);

        // The layer names its memory version, pinned by the profile rather than resolved mid-turn.
        if (VersionOf(layer) is not { Length: > 0 } versionId)
        {
            return EvidenceRefusals.Refuse(
                EvidenceRefusalCodes.SourceNotBound,
                "this layer names no memory version");
        }

        // A layer may pin a scope prefix; absent that, every fact the version holds. The original's store applies this
        // filter while reading; this port's read model takes a version and no query, so it is applied here - over the
        // same facts, and to the same effect.
        var scopePrefix = ScopeOf(layer);

        // Nothing is cached and the pin is not the present, so the pin is read first: a slice recomputed from the feed
        // is what makes "the same pin rebuilds the same facts" true.
        var pin = await _ledger.CurrentPinAsync(cancellationToken).ConfigureAwait(false);
        var slice = await _ledger.SliceAsync(pin, versionId, cancellationToken).ConfigureAwait(false);

        var claims = slice.Facts
            .Select(sliced => ClaimProjection.Of(sliced.Fact, sliced.GlobalSequence))
            .Where(claim => scopePrefix is null
                || (claim.ScopePath is { } path && path.StartsWith(scopePrefix, StringComparison.Ordinal)));

        return new LedgerFactSlice([.. claims]);
    }

    /// <summary>Reads the memory version a layer pinned, or <see langword="null"/> when it pinned none.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The version id, or <see langword="null"/>.</returns>
    private static string? VersionOf(EvidenceLayer layer) =>
        layer.Sources
            .Where(source => source.StartsWith(EvidencePlanes.FactsPrefix, StringComparison.Ordinal))
            .Select(source => source[EvidencePlanes.FactsPrefix.Length..])
            .FirstOrDefault();

    /// <summary>Reads the scope prefix a layer pinned, or <see langword="null"/> when it pinned none.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The scope prefix, or <see langword="null"/>.</returns>
    private static string? ScopeOf(EvidenceLayer layer) =>
        layer.Sources
            .Where(source => source.StartsWith(ScopePrefix, StringComparison.Ordinal))
            .Select(source => source[ScopePrefix.Length..])
            .FirstOrDefault();
}
