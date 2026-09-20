namespace Munarium.Runbooks;

/// <summary>One application pattern: what it is for, and the exemplar to start from.</summary>
/// <remarks>
/// The seven are measured application patterns rather than suggestions, and each names a committed exemplar runbook - so
/// choosing a pattern means starting from a runbook somebody ran, not from a blank page.
/// </remarks>
public sealed record AuthoringPattern
{
    /// <summary>Gets the identity the interview offers and the answers name.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the name an author reads.</summary>
    public required string Name { get; init; }

    /// <summary>Gets what the pattern is, in the terms an author decides in.</summary>
    public required string Description { get; init; }

    /// <summary>Gets the key of the committed exemplar runbook to copy from.</summary>
    public required string StartFrom { get; init; }

    /// <summary>Gets what the pattern is strongest at, and the failure mode to design against.</summary>
    public required string Guidance { get; init; }

    /// <summary>Gets the keys of the shapes the exemplar binds.</summary>
    public IReadOnlyList<string> ShapeNames { get; init; } = [];

    /// <summary>Gets a value indicating whether the pattern carries a RAG completion arm.</summary>
    public bool HasCompletion { get; init; }

    /// <summary>Gets the design notes the deterministic validators cannot police.</summary>
    public IReadOnlyList<string> DecisionNotes { get; init; } = [];
}

/// <summary>The seven patterns, and the rule that only a pattern whose exemplar is available is offered.</summary>
/// <remarks>
/// Upstream embeds every committed exemplar at compile time and serves the patterns it embedded. This port keeps the rule
/// and moves the source of the answer: a deployment says which exemplars it carries, and the interview never offers a
/// choice the catalog would then refuse. That property is the point, and not where the bytes came from.
/// </remarks>
public static class AuthoringCatalog
{
    /// <summary>Gets every pattern the served set holds, in the order the catalog names them.</summary>
    /// <param name="availableExemplars">The exemplar keys the deployment carries, or <see langword="null"/> for all.</param>
    /// <returns>The patterns this deployment can serve.</returns>
    public static IReadOnlyList<AuthoringPattern> Served(IReadOnlyCollection<string>? availableExemplars = null) =>
        availableExemplars is null
            ? All
            : [.. All.Where(pattern => availableExemplars.Contains(pattern.StartFrom, StringComparer.Ordinal))];

    /// <summary>Finds one pattern of the served set.</summary>
    /// <param name="id">The identity to find.</param>
    /// <param name="availableExemplars">The exemplar keys the deployment carries, or <see langword="null"/> for all.</param>
    /// <returns>The pattern, or <see langword="null"/> when this deployment does not serve it.</returns>
    public static AuthoringPattern? Pattern(string? id, IReadOnlyCollection<string>? availableExemplars = null) =>
        id is null
            ? null
            : Served(availableExemplars).FirstOrDefault(
                pattern => string.Equals(pattern.Id, id, StringComparison.Ordinal));

    /// <summary>Gets every pattern as the catalog states it, before the served filter.</summary>
    public static IReadOnlyList<AuthoringPattern> Patterns => All;

    private static readonly AuthoringPattern[] All =
    [
        new()
        {
            Id = "ask-the-corpus",
            Name = "Ask the corpus",
            Description =
                "One question in, clearance-filtered evidence retrieved, a cited answer out - or an honest statement that "
                + "the corpus does not establish this. No conversation and no accumulation: the workhorse pattern.",
            StartFrom = "financial-advisory",
            Guidance =
                "Strongest when a question is answerable from a bounded set of documents. Design against the confident "
                + "answer the corpus does not actually establish - insufficiency is a correct outcome, not a failure.",
            ShapeNames = ["advisory-records"],
            HasCompletion = true,
            DecisionNotes =
            [
                "State the coverage rule in the completion template: a question that asks for an enumerable set must "
                + "demand the whole set, or a partial answer scores as complete.",
                "Uniform level 0 is honest for public corpora - accept the collections.uniform-access Info finding "
                + "rather than inventing a clearance story.",
            ],
        },
        new()
        {
            Id = "research-chat",
            Name = "Research chat",
            Description =
                "Grounded answering made conversational: a session over permitted collections, follow-ups leaning on "
                + "antecedents, history condensed by the client, and the citations of every turn still held to "
                + "resolve-or-insufficient.",
            StartFrom = "regulatory-compliance",
            Guidance =
                "Each turn is independently grounded, so the pattern survives condensed history. Design against the "
                + "model citing a document that search surfaced but the turn never actually read.",
            ShapeNames = ["regulatory-documents"],
            HasCompletion = true,
            DecisionNotes =
            [
                "A search hit you did not read is not a citation - require the turn to fetch what it cites before the "
                + "citation counts.",
                "Sessions pin name and version at creation, so a mid-session runbook upgrade cannot change visibility.",
            ],
        },
        new()
        {
            Id = "red-flag-review",
            Name = "Red-flag review",
            Description =
                "The corpus is interrogated by an extraction pass, source by source; claims meet the ledger; and the "
                + "product is the queue - every place the corpus disagrees with itself, both values and both sources "
                + "attached, awaiting a human verdict.",
            StartFrom = "due-diligence",
            Guidance =
                "The deliverable is the queue, not an answer. Finding recall depends far more on how subjects are "
                + "normalized than on which model runs the pass.",
            ShapeNames = ["dataroom-documents"],
            HasCompletion = false,
            DecisionNotes =
            [
                "Collections are drawn on real governance boundaries - the compartment layout is the review-team access "
                + "model.",
                "Subjects are folded identifiers, so two spellings collide instead of hiding a conflict. Normalizing "
                + "subjects is the highest-leverage change available to an author here.",
            ],
        },
        new()
        {
            Id = "living-knowledge-base",
            Name = "Living knowledge base",
            Description =
                "A knowledge corpus that keeps moving: release notes supersede articles and tickets contradict docs. "
                + "Canon answers what is true now, retrieval shows the language behind that, and corrections move canon "
                + "without deleting history.",
            StartFrom = "support-knowledge",
            Guidance =
                "For corpora where the newest document wins and the superseded one still has to be explainable. Design "
                + "against silent staleness: an outdated answer that still reads as current.",
            ShapeNames = ["knowledge-sources"],
            HasCompletion = true,
            DecisionNotes =
            [
                "A prefix per source system, because ten systems have ten owners.",
                "Keys carry no dots: subject.key splits at the LAST dot, so a key that encodes a version must be "
                + "dash-encoded, as in release_date::4-2-1.",
            ],
        },
        new()
        {
            Id = "entity-intelligence",
            Name = "Entity-centric intelligence",
            Description =
                "Sources describe the same actors under different names. The value is the registry: one canonical "
                + "entity per real-world actor, every alias attached, facts and findings converging instead of "
                + "fragmenting.",
            StartFrom = "threat-intelligence",
            Guidance =
                "The registry is the deliverable. Design against over-merging - collapsing two real actors into one "
                + "entity is a worse error than leaving them apart.",
            ShapeNames = ["threat-reports"],
            HasCompletion = false,
            DecisionNotes =
            [
                "Seed a handful of multi-alias actors and one over-merge trap in a test corpus before trusting the "
                + "alias map on real data.",
                "Value normalization folds defanged versus fanged indicators, so false conflicts disappear while "
                + "genuine conflicts survive.",
            ],
        },
        new()
        {
            Id = "audit-sweeps",
            Name = "Comprehensive audit sweeps",
            Description =
                "Not a question but a mandate: find everything wrong in this corpus. One open-ended prompt is the "
                + "failure mode; the pattern is decomposition - plan targeted sub-questions, run each as a grounded "
                + "ask, audit the plan for coverage, and merge under provenance.",
            StartFrom = "sweep-coverage",
            Guidance =
                "One open-ended prompt is the failure mode; decomposition is the pattern. Coverage comes from auditing "
                + "the plan, not from writing a longer prompt.",
            ShapeNames = ["dataroom-documents"],
            HasCompletion = false,
            DecisionNotes =
            [
                "Two sweep runbooks can share one collection: build the index once and let the applications differ "
                + "only in retrieval policy.",
                "Shapes are shared rather than copied - several runbooks can read one corpus through different "
                + "retrieval architectures.",
            ],
        },
        new()
        {
            Id = "assistant-memory",
            Name = "Long-horizon assistant memory",
            Description =
                "An engagement that outlives any conversation: each new document is a unit - compose the brief from "
                + "memory so far, process, gate the extracted claims, accept into a new version. A lineage whose every "
                + "state is reproducible.",
            StartFrom = "insurance-claims",
            Guidance =
                "For engagements longer than any one conversation. Every state must be reproducible from its lineage, "
                + "so settle the unit boundary before the prompt.",
            ShapeNames = ["claim-files"],
            HasCompletion = false,
            DecisionNotes =
            [
                "Unit as_of dates power date-pinned ledger reads - stamp them from the start.",
                "Witnessed extraction blocks on contradiction while backfill downgrades to a warning and a disputed "
                + "claim. Pick the mode per corpus era, not per taste.",
            ],
        },
    ];
}

