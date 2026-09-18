namespace Munarium.Store.SharpCoreDb;

using Munarium.Ledger;
using Munarium.Retrieval;
using SharpCoreDB.Search.Lexical;
using SharpCoreDB.VectorSearch;
using SharpCoreDB.VectorSearch.Index;

/// <summary>
/// A retrieval backend over SharpCoreDB's indexes: a BM25 lexical leg and a vector leg, fused by rank.
/// </summary>
/// <remarks>
/// The vector leg is SharpCoreDB's <see cref="IVectorIndex"/> - exact by default, or a DiskANN graph when the
/// caller selects it - and the lexical leg is SharpCoreDB's <see cref="FullTextIndex"/>, which is Okapi BM25 over
/// an inverted index with positions, so a phrase in the query can be preferred to the same terms scattered.
/// Fusion is by reciprocal rank, because a BM25 score and a cosine distance do not share a scale and adding them
/// would be arithmetic on units that do not exist.
/// <para>
/// Fusion is this port's own rather than the engine's: the answer and its envelope are produced together here, so
/// the ranking that decided the answer is the ranking the envelope records, and opaque chunk ids stay opaque - the
/// engine's fusion hands a chunk id back as a number.
/// </para>
/// <para>
/// The catalogue is append-only by design. Correcting or re-chunking a document builds a new index version rather
/// than mutating this one, which is what lets an envelope issued yesterday still be verified today.
/// </para>
/// </remarks>
public sealed class SharpCoreDbRetriever : IRetrievalBackend, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<IndexedChunk> _catalogue = [];
    private readonly FullTextIndex _lexical = new();
    private readonly IVectorIndex _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharpCoreDbRetriever"/> class.
    /// </summary>
    /// <param name="dimensions">The embedding width every indexed chunk and query must match.</param>
    /// <param name="indexVersion">The immutable index version this retriever answers from.</param>
    /// <param name="ledgerWatermark">The ledger position the index reflects.</param>
    /// <param name="kind">The vector engine to build the index with.</param>
    public SharpCoreDbRetriever(
        int dimensions,
        string indexVersion,
        SequenceNumber ledgerWatermark,
        VectorIndexKind kind = VectorIndexKind.Exact)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);

        Kind = kind;
        _index = kind switch
        {
            VectorIndexKind.Exact => new FlatIndex(dimensions),
            _ => new DiskAnnIndex(DiskAnnConfig.Default(dimensions)),
        };

        IndexVersion = indexVersion;
        LedgerWatermark = ledgerWatermark;
    }

    /// <summary>Gets the vector engine this index version was built by.</summary>
    public VectorIndexKind Kind { get; }

    /// <summary>Gets the versioned reference a manifest records for that engine.</summary>
    public string Engine => VectorIndexEngines.Of(Kind);

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
            _catalogue.Add(new IndexedChunk(source, text));
            _index.Add(id, embedding);
            _lexical.Add(id, text);
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
            // A leg is asked for at least one candidate, because a leg asked for none cannot be asked at all. What
            // the caller receives is still decided by TopK, which the fusion applies afterwards.
            var candidates = Math.Max(query.TopK, 1) * 2;
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

    // BM25 through the engine's own analyzer, so a query and a document agree on what a term is: the classifying
    // tokenizer, the English stop-word list and the word-only stemmer all live there rather than being re-guessed
    // here. Positions are stored, which is what makes the phrase boost possible.
    private IReadOnlyList<RetrievedChunk> LexicalLeg(string text, int k)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var hits = _lexical.Search(text, k);
        var ranked = new List<RetrievedChunk>(hits.Count);

        foreach (var hit in hits)
        {
            // RRF only reads the order, so the BM25 score travels through as the score.
            var chunk = _catalogue[(int)hit.Id];
            ranked.Add(new RetrievedChunk(chunk.Source, hit.Score, chunk.Text));
        }

        return ranked;
    }

    private sealed record IndexedChunk(SourceReference Source, string Text);
}
