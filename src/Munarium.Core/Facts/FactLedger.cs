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
    /// Reads the facts that were current at a pin, resolving supersession by lineage.
    /// </summary>
    /// <param name="pin">The global position to read as of.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The slice, with a digest over exactly the facts it contains.</returns>
    public async ValueTask<FactSlice> SliceAsync(SequenceNumber pin, CancellationToken cancellationToken = default)
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
            current[fact.Lineage] = new SlicedFact(fact, fact.Verdict(), entry.GlobalSequence);
        }

        var facts = current.Values.OrderBy(sliced => sliced.Fact.Lineage, StringComparer.Ordinal).ToArray();
        return new FactSlice(pin, facts, DigestOf(facts));
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
