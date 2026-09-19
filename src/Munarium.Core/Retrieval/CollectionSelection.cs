namespace Munarium.Retrieval;

/// <summary>
/// One collection's probe pool: what the original query found in it before anything was expanded.
/// </summary>
/// <param name="Collection">The collection's name.</param>
/// <param name="Hits">What the probe found, strongest first as the backend ranked it.</param>
public sealed record CollectionPool(string Collection, IReadOnlyList<RetrievedChunk> Hits);

/// <summary>
/// Ranks the collections a turn probed, strongest first, by the evidence their original-query pools carry.
/// </summary>
/// <remarks>
/// The formula is the original's: <c>strongest × (1 + phraseBoost × phraseFraction)</c>, where the strongest is the sum
/// of the pool's three best scores and the fraction is the share of the pool carrying one of the query's own adjacent
/// content-word pairs verbatim. The default boost of 3 is measured rather than chosen - a pool that is 85% phrase counts
/// 3.55×, one that is 6% counts 1.18× - so strong phrase evidence decides and weak phrase evidence yields to strength.
/// That matters because a phrase can be a later coinage: on "How did colonial newspapers report the Boston Tea Party?"
/// every pool carried it in under 6% of its hits, and a phrase-first order let that noise outrank the newspaper shards
/// that actually hold the December 1773 reports.
/// <para>
/// One difference from the original, written down where it lives: it ranks by two legs - the sum of a pool's three
/// strongest lexical scores blended with the phrase fraction, then the negative sum of its three best vector distances -
/// while this port's backend answers with one fused score per chunk rather than with each leg, so the evidence reads the
/// strong end of that one score and the second key is the collection's name. The shape is the same and the first key
/// carries the decision; what is gone is the ability to prefer the pool that was closer in vector space when two pools
/// are equally strong in a single fused score, which is a comparison the fusion has already made.
/// </para>
/// <para>
/// A pool that came back empty is not a candidate at all: no evidence is not weak evidence, and ranking one would spend
/// a deep search on a collection that has nothing to search.
/// </para>
/// </remarks>
public static class CollectionSelection
{
    /// <summary>
    /// The terms a phrase may not contain, which is the original's list verbatim.
    /// </summary>
    /// <remarks>
    /// Deliberately the original's, with its omissions: a pair like "of the" is not a phrase, and a list that grew a term
    /// here would change which pools count as carrying the query's own words without changing anything a reader could
    /// see - which is exactly the kind of drift a port should not introduce quietly.
    /// </remarks>
    private static readonly string[] Stopwords =
    [
        "i", "me", "my", "myself", "we", "our", "ours", "ourselves", "you", "your", "yours",
        "yourself", "yourselves", "he", "him", "his", "himself", "she", "her", "hers", "herself",
        "it", "its", "itself", "they", "them", "their", "theirs", "themselves", "what", "which",
        "who", "whom", "this", "that", "these", "those", "am", "is", "are", "was", "were", "be",
        "been", "being", "have", "has", "had", "having", "do", "does", "did", "doing", "a", "an",
        "the", "and", "but", "if", "or", "because", "as", "until", "while", "of", "at", "by",
        "for", "with", "about", "against", "between", "into", "through", "during", "before",
        "after", "above", "below", "to", "from", "up", "down", "in", "out", "on", "off", "over",
        "under", "again", "further", "then", "once", "here", "there", "when", "where", "why",
        "how", "all", "any", "both", "each", "few", "more", "most", "other", "some", "such", "no",
        "nor", "not", "only", "own", "same", "so", "than", "too", "very", "s", "t", "can", "will",
        "just", "don", "should", "now",
    ];

    /// <summary>Splits text into its words: lower-cased, split on anything that is not a letter or a digit.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The tokens, in order.</returns>
    public static IReadOnlyList<string> WordTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            while (start < text.Length && !char.IsLetterOrDigit(text[start]))
            {
                start++;
            }

            var end = start;

            while (end < text.Length && char.IsLetterOrDigit(text[end]))
            {
                end++;
            }

            if (end > start)
            {
                tokens.Add(text[start..end].ToLowerInvariant());
            }

            start = end;
        }

        return tokens;
    }

    /// <summary>Reads the query's own adjacent content-word pairs, in query order and de-duplicated.</summary>
    /// <remarks>
    /// For "What cities did George Washington visit?" they are <c>george washington</c> and <c>washington visit</c>:
    /// "cities" and "george" are separated by a stop word, so they are not a phrase. A query with no adjacent content
    /// words has no phrases, and the phrase half of the evidence then contributes nothing - which is the honest answer,
    /// because there is no phrase to carry.
    /// </remarks>
    /// <param name="query">The query.</param>
    /// <returns>The phrases, as token pairs.</returns>
    public static IReadOnlyList<(string First, string Second)> QueryPhrases(string query)
    {
        var tokens = WordTokens(query);
        var phrases = new List<(string First, string Second)>();

        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            var pair = (First: tokens[index], Second: tokens[index + 1]);

            if (Stopwords.Contains(pair.First) || Stopwords.Contains(pair.Second))
            {
                continue;
            }

            if (!phrases.Contains(pair))
            {
                phrases.Add(pair);
            }
        }

        return phrases;
    }

    /// <summary>Reports whether any of the query's phrases occurs verbatim in the text.</summary>
    /// <param name="text">The text to read.</param>
    /// <param name="phrases">The phrases.</param>
    /// <returns><see langword="true"/> when one of them is adjacent in the text.</returns>
    public static bool HasPhrase(string text, IReadOnlyList<(string First, string Second)> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);

        if (phrases.Count == 0)
        {
            return false;
        }

        var tokens = WordTokens(text);

        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (phrases.Contains((First: tokens[index], Second: tokens[index + 1])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ranks the pools by the evidence they carry, strongest first.</summary>
    /// <remarks>
    /// Every non-empty pool is ranked rather than only the strongest few, because the caller's merge reads the rank of
    /// each one: a collection that lost the selection still contributed its probe pool to the answer, and a rank that
    /// stopped at the selection would have to be invented for the rest. Each pool's evidence is computed once, so the
    /// ordering compares measured numbers rather than calling a function twice per comparison.
    /// </remarks>
    /// <param name="pools">The pools, one per collection that was probed.</param>
    /// <param name="query">The query they were probed with.</param>
    /// <param name="phraseBoost">How strongly phrase evidence multiplies strength.</param>
    /// <returns>The pools' positions, strongest first.</returns>
    public static IReadOnlyList<int> Rank(
        IReadOnlyList<CollectionPool> pools,
        string query,
        double phraseBoost = 3.0)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(query);

        var phrases = QueryPhrases(query);
        var boost = double.IsFinite(phraseBoost) ? Math.Max(0, phraseBoost) : 0;
        var evidence = new double[pools.Count];
        var ranked = new List<int>();

        for (var index = 0; index < pools.Count; index++)
        {
            if (pools[index].Hits.Count == 0)
            {
                continue;
            }

            evidence[index] = Evidence(pools[index], phrases, boost);
            ranked.Add(index);
        }

        ranked.Sort((left, right) =>
        {
            var comparison = evidence[right].CompareTo(evidence[left]);

            return comparison != 0
                ? comparison
                : string.CompareOrdinal(pools[left].Collection, pools[right].Collection);
        });

        return ranked;
    }

    /// <summary>One pool's strength, blended with the share of it that carries a query phrase.</summary>
    /// <remarks>
    /// A pool whose scores are none of them finite has no strength to read, and zero is what that is: the original uses
    /// a negative infinity for a pool missing one of its two legs, and with a single fused score there is no leg to be
    /// missing - a pool of chunks nobody scored is a pool with nothing to say.
    /// </remarks>
    /// <param name="pool">The pool.</param>
    /// <param name="phrases">The query's phrases.</param>
    /// <param name="phraseBoost">How strongly phrase evidence multiplies strength.</param>
    /// <returns>The evidence.</returns>
    private static double Evidence(
        CollectionPool pool,
        IReadOnlyList<(string First, string Second)> phrases,
        double phraseBoost)
    {
        var share = (double)pool.Hits.Count(hit => HasPhrase(hit.Text, phrases)) / pool.Hits.Count;
        var strongest = pool.Hits
            .Select(hit => hit.Score)
            .Where(double.IsFinite)
            .OrderByDescending(score => score)
            .Take(3)
            .Sum();

        return strongest * (1.0 + (phraseBoost * share));
    }
}
