namespace Munarium.Core.Tests.Retrieval;

using Munarium.Retrieval;

/// <summary>
/// Tests for the collection ranking: which pooled evidence decides, and what the phrase half of it is worth.
/// </summary>
public class CollectionSelectionTests
{
    private const string Query = "What cities did George Washington visit?";

    /// <summary>
    /// The original's own numbers, which is why the boost is three: at the default, 85% phrase evidence counts 3.55× and
    /// 6% counts 1.18×, so a pool that carries the query's phrase throughout beats one three times as strong that
    /// carries it barely - and a pool that is more than three and a half times as strong is not caught by that.
    /// </summary>
    [Fact]
    public void PhraseEvidenceDecidesAgainstStrengthAtTheOriginalThreshold()
    {
        var phrases = Pool("phrases", hits: 20, carrying: 17, score: 1.0 / 3.0);
        var threeTimesStronger = Pool("strength", hits: 50, carrying: 3, score: 1.0);

        Assert.Equal(["phrases", "strength"], Rank(Query, phrases, threeTimesStronger));

        var threeAndAHalfTimesStronger = Pool("strength", hits: 50, carrying: 3, score: 1.21);

        Assert.Equal(["strength", "phrases"], Rank(Query, phrases, threeAndAHalfTimesStronger));
    }

    /// <summary>The query's own adjacent content-word pairs are its phrases, and a stop word breaks a pair.</summary>
    /// <remarks>
    /// "cities" and "george" are separated by "did", so they are not a phrase - which is the original's example and the
    /// reason a phrase is a pair of tokens rather than any two content words in the query.
    /// </remarks>
    [Fact]
    public void TheQuerysOwnAdjacentContentWordsAreItsPhrases()
    {
        Assert.Equal(
            ["what", "cities", "did", "george", "washington", "visit"],
            CollectionSelection.WordTokens(Query));

        var expected = new List<(string First, string Second)>
        {
            ("george", "washington"),
            ("washington", "visit"),
        };

        Assert.Equal(expected, CollectionSelection.QueryPhrases(Query));
    }

    /// <summary>A phrase has to be adjacent, and neither case nor punctuation may hide it.</summary>
    [Fact]
    public void APhraseIsAdjacentRegardlessOfCaseAndPunctuation()
    {
        var phrases = CollectionSelection.QueryPhrases(Query);

        Assert.True(CollectionSelection.HasPhrase("...George, Washington! The colonies...", phrases));

        // Both words are there and the phrase is not: adjacency is the whole claim.
        Assert.False(CollectionSelection.HasPhrase("Washington visited George that spring.", phrases));
    }

    /// <summary>An empty pool is not a candidate: no evidence is not weak evidence.</summary>
    [Fact]
    public void APoolWithNoHitsIsNotACandidate()
    {
        var ranked = CollectionSelection.Rank(
            [new CollectionPool("empty", []), Pool("contracts", hits: 4, carrying: 0, score: 1.0)],
            Query);

        Assert.Equal([1], ranked);
    }

    /// <summary>Two pools of equal evidence are ordered by name, so the ranking is total.</summary>
    [Fact]
    public void EqualEvidenceIsOrderedByName()
    {
        var ranked = Rank(
            Query,
            Pool("minutes", hits: 4, carrying: 0, score: 1.0),
            Pool("contracts", hits: 4, carrying: 0, score: 1.0));

        Assert.Equal(["contracts", "minutes"], ranked);
    }

    /// <summary>A query with no adjacent content words has no phrases, so strength alone decides.</summary>
    [Fact]
    public void AQueryWithNoPhrasesRanksByStrengthAlone()
    {
        var ranked = Rank(
            "what is the",
            Pool("weak", hits: 10, carrying: 10, score: 0.5),
            Pool("strong", hits: 10, carrying: 0, score: 2.0));

        // "strong" is 6.0 of strength against "weak"'s 1.5, and there is no phrase for the blend to find.
        Assert.Equal(["strong", "weak"], ranked);
    }

    /// <summary>The ranking as collection names, strongest first.</summary>
    /// <param name="query">The query the pools were probed with.</param>
    /// <param name="pools">The pools.</param>
    /// <returns>The names.</returns>
    private static List<string> Rank(string query, params CollectionPool[] pools) =>
    [
        .. CollectionSelection
            .Rank([.. pools], query)
            .Select(index => pools[index].Collection),
    ];

    /// <summary>A pool of one collection, with the phrase carried by the share asked for.</summary>
    /// <param name="collection">The collection's name.</param>
    /// <param name="hits">How many hits the probe found.</param>
    /// <param name="carrying">How many of them carry the query's phrase.</param>
    /// <param name="score">What each hit scored.</param>
    /// <returns>The pool.</returns>
    private static CollectionPool Pool(string collection, int hits, int carrying, double score)
    {
        var chunks = new List<RetrievedChunk>();

        for (var index = 0; index < hits; index++)
        {
            chunks.Add(Chunk(
                index < carrying ? "the George Washington papers" : "colonies and commerce",
                score));
        }

        return new CollectionPool(collection, chunks);
    }

    /// <summary>One retrieved chunk of one source.</summary>
    /// <param name="text">Its text.</param>
    /// <param name="score">Its fused score.</param>
    /// <returns>The chunk.</returns>
    private static RetrievedChunk Chunk(string text, double score) =>
        new(new SourceReference("chunk-1", "src-1", "docs/a.md", "sha", 0), score, text);
}
