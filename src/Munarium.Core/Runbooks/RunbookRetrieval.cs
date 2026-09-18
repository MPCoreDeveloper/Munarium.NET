namespace Munarium.Runbooks;

using Munarium.Evidence;

/// <summary>
/// Add retrieval vocabulary when a query contains at least one configured trigger term.
/// </summary>
/// <remarks>
/// The terms are application policy: the retrieval engine only performs case-insensitive, whole-token matching and
/// appends the additions. No domain vocabulary is compiled into the engine.
/// </remarks>
public sealed record QueryExpansionSpec
{
    /// <summary>Gets the terms that trigger the expansion.</summary>
    public IReadOnlyList<string> WhenAny { get; init; } = [];

    /// <summary>Gets the terms the expansion adds.</summary>
    public IReadOnlyList<string> AddTerms { get; init; } = [];
}

/// <summary>
/// Ask the runbook's query-expansion model for generic lexical variants at turn time.
/// </summary>
/// <remarks>
/// The engine supplies the safety-constrained prompt; the runbook controls whether the paid step runs and how much it
/// may produce.
/// </remarks>
public sealed record ModelQueryExpansionSpec
{
    /// <summary>Gets the most terms an expansion may add.</summary>
    public int MaxTerms { get; init; } = 12;

    /// <summary>
    /// Gets the token ceiling for the call, or <see langword="null"/> for the server's configured budget.
    /// </summary>
    /// <remarks>
    /// Absent means the server's <c>query_expansion</c> budget rather than a grammar constant. Validated to 32..=512
    /// when declared.
    /// </remarks>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Gets a value indicating whether an expansion failure fails the turn.
    /// </summary>
    /// <remarks>
    /// False, the default, falls back to the original query; true makes a provider or parse failure the turn's failure.
    /// </remarks>
    public bool Required { get; init; }
}

/// <summary>
/// Generic two-stage collection selection for wide, sharded runbooks.
/// </summary>
/// <remarks>
/// A bounded original-query probe chooses the strongest collections before the full candidate pool and optional model
/// expansion are evaluated.
/// </remarks>
public sealed record CollectionSelectionSpec
{
    /// <summary>Gets how many collections the probe may select.</summary>
    public required int MaxCollections { get; init; }

    /// <summary>Gets how many candidates the probe ranks.</summary>
    public long ProbeCandidateN { get; init; } = 50;

    /// <summary>Gets how many candidates each collection contributes to the deep search.</summary>
    public int CandidatePoolPerCollection { get; init; } = 100;

    /// <summary>
    /// Gets how strongly a collection's phrase evidence multiplies its density evidence.
    /// </summary>
    /// <remarks>
    /// The formula is <c>density × (1 + phraseBoost × fraction)</c>, where the fraction is the share of the probe pool
    /// carrying one of the query's own adjacent content-word pairs verbatim. Zero means density only; the default of 3
    /// makes a pool 85% carrying the phrase count 3.55× and one carrying it in 6% count 1.18×, so strong phrase evidence
    /// decides and weak phrase evidence yields to density.
    /// </remarks>
    public double PhraseBoost { get; init; } = 3.0;
}

/// <summary>
/// Route a query to an application-owned subset of the runbook's collections.
/// </summary>
/// <remarks>
/// This is a candidate-selection instruction and not corpus knowledge in the engine: the engine only performs the
/// matching, and the routing is the deployment's.
/// </remarks>
public sealed record CollectionRouteSpec
{
    /// <summary>Gets the terms that all have to be present for the route to apply.</summary>
    public IReadOnlyList<string> WhenAll { get; init; } = [];

    /// <summary>Gets the collections the route narrows to.</summary>
    public IReadOnlyList<string> Collections { get; init; } = [];
}

/// <summary>How a demotion marker is matched.</summary>
public enum DemotionMatch
{
    /// <summary>A case-insensitive substring of the chunk text: exact, but every candidate row is detoasted and lowered.</summary>
    Substring = 0,

    /// <summary>The marker's words in sequence in the chunk's own parsed vector, stemmed and punctuation-insensitive.</summary>
    Phrase = 1,
}

/// <summary>
/// Demote text carrying an application-defined marker without excluding it.
/// </summary>
public sealed record ContentDemotionSpec
{
    /// <summary>Gets the marker.</summary>
    public required string Contains { get; init; }

    /// <summary>Gets the multiplier applied to a matching hit's lexical contribution.</summary>
    public double LexicalMultiplier { get; init; } = 1.0;

    /// <summary>Gets the penalty added to a matching hit's vector distance.</summary>
    public double VectorDistancePenalty { get; init; }

    /// <summary>
    /// Gets the collections the rule does not apply to.
    /// </summary>
    /// <remarks>
    /// A corpus-structure declaration rather than a query rule: in a catalog collection the "metadata-only" record IS
    /// the content, so demoting it there excludes the collection's only answers.
    /// </remarks>
    public IReadOnlyList<string> ExceptCollections { get; init; } = [];

    /// <summary>Gets how the marker is matched.</summary>
    public DemotionMatch Match { get; init; } = DemotionMatch.Substring;
}

/// <summary>
/// Weighted reciprocal-rank fusion for the cross-collection merge.
/// </summary>
/// <remarks>
/// Each leg contributes <c>weight / (rrfK + global rank)</c>, and the defaults reproduce the unweighted merge
/// byte-for-byte. The collection-evidence weight adds a third leg fed by the selection's own ranking - every hit also
/// scores <c>weight / (rrfK + rank of its collection)</c> - so a collection the probe showed to be ABOUT the query's
/// subject lends its chunks a prior that a collection merely USING the words does not get. Without a selection that leg
/// has nothing to read and contributes nothing.
/// </remarks>
public sealed record FusionSpec
{
    /// <summary>Gets the lexical leg's weight.</summary>
    public double LexicalWeight { get; init; } = 1.0;

    /// <summary>Gets the vector leg's weight.</summary>
    public double VectorWeight { get; init; } = 1.0;

    /// <summary>Gets the collection-evidence leg's weight.</summary>
    public double CollectionEvidenceWeight { get; init; }

    /// <summary>
    /// Gets the multiplier on the leg contributions of hits that came from the unselected collections' probe pools.
    /// </summary>
    /// <remarks>
    /// Those pools are ranked as their own stratum, so their raw scores are not comparable with the deep search's: 1.0
    /// means a probe rank-1 counts like a deep rank-1 and the collection-evidence leg arbitrates, while lower values
    /// favour the deep search.
    /// </remarks>
    public double UnselectedPoolWeight { get; init; } = 1.0;
}

/// <summary>
/// The retrieval knobs a runbook declares.
/// </summary>
public sealed record RetrievalSpec
{
    /// <summary>Gets how many hits a turn serves.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Gets the reciprocal-rank fusion constant.</summary>
    public double RrfK { get; init; } = 60.0;

    /// <summary>Gets how many candidates each leg ranks.</summary>
    public long CandidateN { get; init; } = 50;

    /// <summary>
    /// Gets how many collections are searched concurrently per turn.
    /// </summary>
    /// <remarks>
    /// Each in-flight search holds one pooled connection, which is why this is bounded: probing 58 shards sequentially
    /// under a loaded database took about 4.5 seconds per shard, and the response's first byte - the first progress
    /// event - went out after all of them, past the ingress timeout.
    /// </remarks>
    public int SearchConcurrency { get; init; } = 4;

    /// <summary>Gets how many of the query's normalized lexemes a chunk has to hold to enter the lexical pool.</summary>
    /// <remarks>
    /// One accepts any single word, which is the OR leg's full behaviour; two requires a pair, which drops the rows that
    /// match one usually common word - the bulk of what an OR query over a large shard has to scan and rank.
    /// </remarks>
    public int MinimumShouldMatch { get; init; } = 1;

    /// <summary>
    /// Gets the fraction above which a lexeme is a stop term for a collection.
    /// </summary>
    /// <remarks>
    /// Zero, the default, disables it. The frequencies come from the corpus itself, so the engine learns that a word is
    /// ordinary in a shard that is about it and not elsewhere; if every lexeme is frequent the full set is kept, because
    /// the predicate is never allowed to be empty.
    /// </remarks>
    public double StopTermFraction { get; init; }

    /// <summary>Gets the evidence hierarchies this runbook offers.</summary>
    public IReadOnlyList<ResearchProfile> ResearchProfiles { get; init; } = [];

    /// <summary>Gets the profile a turn gets when it names none, or <see langword="null"/> for the document path.</summary>
    public string? DefaultResearchProfile { get; init; }

    /// <summary>Gets the conditional vocabulary this runbook supplies.</summary>
    public IReadOnlyList<QueryExpansionSpec> QueryExpansions { get; init; } = [];

    /// <summary>Gets the relative contribution of the expanded query to the two legs.</summary>
    public double QueryExpansionWeight { get; init; } = 1.0;

    /// <summary>Gets the provider-backed generic lexical expansion, when one is declared.</summary>
    public ModelQueryExpansionSpec? ModelQueryExpansion { get; init; }

    /// <summary>Gets the evidence-driven narrowing for runbooks with many collections, when declared.</summary>
    public CollectionSelectionSpec? CollectionSelection { get; init; }

    /// <summary>Gets the weighted fusion, or <see langword="null"/> for the unweighted two-leg merge.</summary>
    public FusionSpec? Fusion { get; init; }

    /// <summary>Gets the conditional collection routing.</summary>
    public IReadOnlyList<CollectionRouteSpec> CollectionRoutes { get; init; } = [];

    /// <summary>Gets the content markers and penalties.</summary>
    public IReadOnlyList<ContentDemotionSpec> ContentDemotions { get; init; } = [];
}
