namespace Munarium.Retrieval;

using Munarium.Ledger;

/// <summary>
/// Fuses ranked candidate lists with reciprocal rank fusion and seals the provenance envelope over
/// the fused answer.
/// </summary>
/// <remarks>
/// RRF is used because the legs are not comparable - a lexical rank and a vector distance live on
/// different scales - so fusing their <em>ranks</em> avoids inventing a common score. The tie-break
/// is the chunk id, so identical inputs always fuse to an identical answer, and therefore to an
/// identical envelope.
/// </remarks>
public static class ReciprocalRankFusion
{
    /// <summary>The conventional RRF constant.</summary>
    public const int DefaultK = 60;

    /// <summary>
    /// Fuses ranked lists into one answer with a provenance envelope.
    /// </summary>
    /// <param name="rankings">The candidate lists, best first, one per retrieval leg.</param>
    /// <param name="indexVersion">The index version the candidates came from.</param>
    /// <param name="ledgerWatermark">The ledger position the index reflects.</param>
    /// <param name="topK">How many fused chunks the answer may carry.</param>
    /// <param name="k">The RRF constant.</param>
    /// <returns>The fused answer and its envelope.</returns>
    public static RetrievalResult Fuse(
        IReadOnlyList<IReadOnlyList<RetrievedChunk>> rankings,
        string indexVersion,
        SequenceNumber ledgerWatermark,
        int topK,
        int k = DefaultK)
    {
        ArgumentNullException.ThrowIfNull(rankings);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var chunks = new Dictionary<string, RetrievedChunk>(StringComparer.Ordinal);

        foreach (var ranking in rankings)
        {
            for (var rank = 0; rank < ranking.Count; rank++)
            {
                var chunk = ranking[rank];
                var chunkId = chunk.Source.ChunkId;

                scores[chunkId] = scores.GetValueOrDefault(chunkId) + (1.0 / (k + rank + 1));
                chunks[chunkId] = chunk;
            }
        }

        var fused = chunks.Keys
            .OrderByDescending(chunkId => scores[chunkId])
            .ThenBy(chunkId => chunkId, StringComparer.Ordinal)
            .Take(topK)
            .Select(chunkId => chunks[chunkId] with { Score = scores[chunkId] })
            .ToArray();

        var envelope = new ProvenanceEnvelope(
            indexVersion,
            ledgerWatermark,
            [.. fused.Select(chunk => chunk.Source)]);

        return new RetrievalResult(fused, envelope);
    }
}
