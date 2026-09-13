namespace Munarium.Store.SharpCoreDb;

using System.Text;
using Munarium.Ledger;
using Munarium.Retrieval;
using SharpCoreDB.VectorSearch;

/// <summary>
/// A retrieval backend over SharpCoreDB's vector index, fused with a lexical leg by RRF.
/// </summary>
/// <remarks>
/// The vector leg is SharpCoreDB's <see cref="IVectorIndex"/>; the lexical leg is a deterministic
/// term-overlap ranker. RRF is what keeps those two comparable, because a vector distance and a
/// term count do not share a scale.
/// <para>
/// The catalogue is append-only by design. Correcting or re-chunking a document builds a new index
/// version rather than mutating this one, which is what lets an envelope issued yesterday still be
/// verified today.
/// </para>
/// </remarks>
public sealed class SharpCoreDbRetriever : IRetrievalBackend, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<IndexedChunk> _catalogue = [];
    private readonly IVectorIndex _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharpCoreDbRetriever"/> class.
    /// </summary>
    /// <param name="dimensions">The embedding width every indexed chunk and query must match.</param>
    /// <param name="indexVersion">The immutable index version this retriever answers from.</param>
    /// <param name="ledgerWatermark">The ledger position the index reflects.</param>
    public SharpCoreDbRetriever(int dimensions, string indexVersion, SequenceNumber ledgerWatermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);

        // Exact search. HNSW is the drop-in once a corpus outgrows brute force; the seam does not change.
        _index = new FlatIndex(dimensions);
        IndexVersion = indexVersion;
        LedgerWatermark = ledgerWatermark;
    }

    /// <summary>Gets the index version this retriever answers from.</summary>
    public string IndexVersion { get; }

    /// <summary>Gets the ledger position this index reflects.</summary>
    public SequenceNumber LedgerWatermark { get; }

    /// <summary>Gets the number of indexed chunks.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _catalogue.Count;
            }
        }
    }

    /// <summary>
    /// Adds a chunk to the index version.
    /// </summary>
    /// <param name="source">Where the chunk came from.</param>
    /// <param name="text">The chunk text.</param>
    /// <param name="embedding">The chunk's embedding, which comes from the provider layer.</param>
    public void Index(SourceReference source, string text, ReadOnlySpan<float> embedding)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            var id = _catalogue.Count;
            _catalogue.Add(new IndexedChunk(source, text, Tokenize(text)));
            _index.Add(id, embedding);
        }
    }

    /// <inheritdoc />
    public ValueTask<RetrievalResult> SearchAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var candidates = query.TopK > 0 ? query.TopK * 2 : 0;
            var legs = new List<IReadOnlyList<RetrievedChunk>>(2);

            if (!query.Embedding.IsEmpty)
            {
                if (query.Embedding.Length != _index.Dimensions)
                {
                    throw new ArgumentException(
                        $"The query embedding has {query.Embedding.Length} dimensions; this index expects {_index.Dimensions}.",
                        nameof(query));
                }

                legs.Add(VectorLeg(query.Embedding.Span, candidates));
            }

            legs.Add(LexicalLeg(query.Text, candidates));

            return ValueTask.FromResult(
                ReciprocalRankFusion.Fuse(legs, IndexVersion, LedgerWatermark, query.TopK));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _index.Dispose();

    private IReadOnlyList<RetrievedChunk> VectorLeg(ReadOnlySpan<float> embedding, int k)
    {
        var hits = _index.Search(embedding, k);
        var ranked = new List<RetrievedChunk>(hits.Count);

        foreach (var hit in hits)
        {
            // RRF only reads the order, so the raw distance travels through as the score.
            var chunk = _catalogue[(int)hit.Id];
            ranked.Add(new RetrievedChunk(chunk.Source, hit.Distance, chunk.Text));
        }

        return ranked;
    }

    private IReadOnlyList<RetrievedChunk> LexicalLeg(string text, int k)
    {
        var terms = Tokenize(text);
        if (terms.Count == 0)
        {
            return [];
        }

        var scored = new List<(IndexedChunk Chunk, int Hits)>();

        foreach (var chunk in _catalogue)
        {
            var hits = terms.Count(chunk.Terms.Contains);

            if (hits > 0)
            {
                scored.Add((chunk, hits));
            }
        }

        return
        [
            .. scored
                .OrderByDescending(entry => entry.Hits)
                .ThenBy(entry => entry.Chunk.Source.ChunkId, StringComparer.Ordinal)
                .Take(k)
                .Select(entry => new RetrievedChunk(entry.Chunk.Source, entry.Hits, entry.Chunk.Text)),
        ];
    }

    private static HashSet<string> Tokenize(string text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        var token = new StringBuilder();

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
                continue;
            }

            Flush(terms, token);
        }

        Flush(terms, token);
        return terms;
    }

    private static void Flush(HashSet<string> terms, StringBuilder token)
    {
        if (token.Length > 0)
        {
            terms.Add(token.ToString());
            token.Clear();
        }
    }

    private sealed record IndexedChunk(SourceReference Source, string Text, HashSet<string> Terms);
}
