namespace Munarium.Facts;

using System.Security.Cryptography;
using Munarium.Ledger;

/// <summary>
/// Reads the ledger as facts: resolves supersession along each lineage and returns the state that
/// was current at a pin.
/// </summary>
/// <remarks>
/// Nothing is cached or materialised. A slice is recomputed from the ledger every time, which is
/// what makes an <c>as_of</c> pin reproducible: the same pin rebuilds the same facts and therefore
/// the same digest.
/// </remarks>
/// <param name="storage">The ledger's storage seam.</param>
public sealed class FactLedger(IStorageBackend storage)
{
    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    /// <summary>
    /// Reads the facts that were current at a pin, across every version.
    /// </summary>
    /// <param name="pin">The global position to read as of.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The slice, with a digest over exactly the facts it contains.</returns>
    public ValueTask<FactSlice> SliceAsync(SequenceNumber pin, CancellationToken cancellationToken = default) =>
        SliceAsync(pin, string.Empty, cancellationToken);

    /// <summary>
    /// Reads the facts that were current at a pin, for one version or for all of them.
    /// </summary>
    /// <param name="pin">The global position to read as of.</param>
    /// <param name="versionId">The version to read, or an empty string for every version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The slice, with a digest over exactly the facts it contains.</returns>
    public async ValueTask<FactSlice> SliceAsync(
        SequenceNumber pin,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _storage.ReadGlobalAsync(pin, cancellationToken).ConfigureAwait(false);

        // The ledger is read in global order, so the last fact seen on a lineage is the current one.
        var current = new Dictionary<string, SlicedFact>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!FactCodec.IsFactEvent(entry.Event.Type))
            {
                continue;
            }

            var fact = FactCodec.Decode(entry.Event.Payload.Span);

            if (versionId.Length > 0 && !string.Equals(fact.VersionId, versionId, StringComparison.Ordinal))
            {
                continue;
            }

            current[fact.Lineage] = new SlicedFact(fact, fact.Verdict(), entry.GlobalSequence);
        }

        var facts = current.Values.OrderBy(sliced => sliced.Fact.Lineage, StringComparer.Ordinal).ToArray();
        return new FactSlice(pin, facts, DigestOf(facts));
    }

    /// <summary>
    /// The current global position: what a caller pins at to read the present state.
    /// </summary>
    /// <remarks>
    /// A stream head is not a pin - a pin is a position in the global feed - so a caller that wants
    /// "everything as of now" has to be able to ask for this. Nothing is cached, so this is a read of
    /// the feed, exactly like the slice it is used for.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last global sequence written, or zero when nothing has been.</returns>
    public async ValueTask<SequenceNumber> CurrentPinAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _storage
            .ReadGlobalAsync(new SequenceNumber(long.MaxValue), cancellationToken)
            .ConfigureAwait(false);

        return entries.Count == 0 ? SequenceNumber.Zero : entries[^1].GlobalSequence;
    }

    /// <summary>
    /// Reads one fact by its claim identity, with whatever now holds its lineage in its place.
    /// </summary>
    /// <remarks>
    /// The resolution a slice uses, at the present pin: a lineage is held by the last fact written to it, so what
    /// supersedes a claim is the fact the ledger would serve instead of it. Read from the feed rather than kept
    /// beside it, like every other read here, so this answer cannot disagree with what a slice answers.
    /// <para>
    /// A claim identity is unique within its stream, so the same identity can be written in two versions. The
    /// earliest write under an identity is the claim, because a caller holds an identity it was given rather than a
    /// later claim that reused it.
    /// </para>
    /// </remarks>
    /// <param name="claimId">The claim to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The claim and its superseder, or <see langword="null"/> when nothing was written under that identity.
    /// </returns>
    public async ValueTask<ClaimRead?> ReadClaimAsync(
        string claimId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimId);

        var entries = await _storage
            .ReadGlobalAsync(new SequenceNumber(long.MaxValue), cancellationToken)
            .ConfigureAwait(false);

        SlicedFact? claim = null;
        SlicedFact? holder = null;

        foreach (var entry in entries)
        {
            if (!FactCodec.IsFactEvent(entry.Event.Type))
            {
                continue;
            }

            var fact = FactCodec.Decode(entry.Event.Payload.Span);

            if (claim is null)
            {
                if (string.Equals(fact.ClaimId, claimId, StringComparison.Ordinal))
                {
                    claim = new SlicedFact(fact, fact.Verdict(), entry.GlobalSequence);
                }

                // Facts that precede the claim cannot supersede it, so the search for the holder starts once the
                // claim itself has been seen.
                continue;
            }

            if (string.Equals(fact.Lineage, claim.Fact.Lineage, StringComparison.Ordinal))
            {
                holder = new SlicedFact(fact, fact.Verdict(), entry.GlobalSequence);
            }
        }

        return claim is null ? null : new ClaimRead(claim, holder);
    }

    private static string DigestOf(IReadOnlyList<SlicedFact> facts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var sliced in facts)
        {
            hash.AppendData(FactCodec.Encode(sliced.Fact));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
