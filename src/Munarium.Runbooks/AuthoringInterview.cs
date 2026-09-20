namespace Munarium.Runbooks;

/// <summary>One section of the authoring interview, in the order the decisions are hard to revise.</summary>
/// <param name="Id">The section identity, which is the prefix of its question ids.</param>
/// <param name="Title">What the author is deciding.</param>
/// <param name="DocRef">The chapter that teaches this decision in full.</param>
/// <param name="Questions">The questions, in the order they are asked.</param>
public sealed record InterviewSection(
    string Id,
    string Title,
    string DocRef,
    IReadOnlyList<InterviewQuestion> Questions);

/// <summary>One question, with the rule attached to the moment the decision is made.</summary>
/// <param name="Id">The answer key: answers are one flat map keyed by question id.</param>
/// <param name="Prompt">What is asked.</param>
/// <param name="Guidance">The rule, read here rather than a chapter away.</param>
/// <param name="Kind">How the answer is shaped, from <see cref="InterviewKinds"/>.</param>
/// <param name="Required">Whether an answer is needed for the draft to materialize.</param>
/// <param name="Default">What materialization uses when the answer is absent.</param>
/// <param name="Choices">The permitted answers of an enum, empty otherwise.</param>
/// <param name="MapsTo">The slot the answer lands in, as documentation rather than a path.</param>
public sealed record InterviewQuestion(
    string Id,
    string Prompt,
    string Guidance,
    string Kind,
    bool Required,
    object? Default,
    IReadOnlyList<string> Choices,
    string MapsTo);

/// <summary>The answer shapes an interview question can carry.</summary>
public static class InterviewKinds
{
    /// <summary>A single line.</summary>
    public const string Line = "string";

    /// <summary>Free prose.</summary>
    public const string Text = "text";

    /// <summary>A whole number.</summary>
    public const string Number = "int";

    /// <summary>A yes or no.</summary>
    public const string Bool = "bool";

    /// <summary>One of <see cref="InterviewQuestion.Choices"/>.</summary>
    public const string Enum = "enum";

    /// <summary>An ordered list of areas, each with a path.</summary>
    public const string Areas = "areas";

    /// <summary>A list of extra fact-body fields.</summary>
    public const string Fields = "fields";

    /// <summary>An answer per key, as in a level or media type per area.</summary>
    public const string Map = "map";
}

/// <summary>The design decisions of a draft runbook set, as an ordered question set.</summary>
/// <remarks>
/// The sections are in the order of how hard each decision is to revise - prefix layout is immutable once documents are
/// uploaded, while retrieval knobs are a new runbook version away - so an author settles what cannot be moved first.
/// <para>
/// The completion section follows the pattern: a pattern with no completion arm is not asked how it should answer
/// questions. Asking about no pattern offers everything, because nothing has been ruled out yet.
/// </para>
/// <para>
/// Upstream attaches a paragraph of guidance to each question. This port keeps the rule each one teaches in a sentence
/// and names the chapter that teaches it in full, so the guidance still arrives with the decision.
/// </para>
/// </remarks>
public static class AuthoringInterview
{
    /// <summary>Builds the interview for a draft.</summary>
    /// <param name="pattern">The chosen pattern, or <see langword="null"/> while none is chosen.</param>
    /// <param name="availableExemplars">The exemplar keys the deployment carries, which bounds the offered patterns.</param>
    /// <returns>The sections, in the order they are asked.</returns>
    public static IReadOnlyList<InterviewSection> For(
        AuthoringPattern? pattern,
        IReadOnlyCollection<string>? availableExemplars = null)
    {
        var choices = AuthoringCatalog
            .Served(availableExemplars)
            .Select(served => served.Id)
            .ToArray();

        List<InterviewSection> sections =
        [
            new(
                "identity",
                "What are you building?",
                "dev-guide 19, Choosing your pattern",
                [
                    new(
                        "identity.description",
                        "Describe the corpus and the question this application answers.",
                        "One or two sentences. This becomes the runbook's header comment and, with the AI assist, the "
                        + "corpus description it drafts from.",
                        InterviewKinds.Text,
                        Required: true,
                        Default: null,
                        Choices: [],
                        MapsTo: "runbook:header-comment"),
                    new(
                        "identity.pattern",
                        "Which application pattern fits?",
                        "Pick by smell: contradiction matters means red-flag-review; naming hides it means "
                        + "entity-intelligence; time matters means living-knowledge-base; obligations matter means "
                        + "assistant-memory; find everything means audit-sweeps, and otherwise ask-the-corpus or "
                        + "research-chat. Each pattern names a committed exemplar to copy from.",
                        InterviewKinds.Enum,
                        Required: false,
                        Default: null,
                        Choices: choices,
                        MapsTo: "pattern"),
                ]),
            new(
                "prefix-layout",
                "Prefix layout - IMMUTABLE once documents are uploaded",
                "dev-guide 16, Prefix design is access design",
                [
                    new(
                        "prefix.root",
                        "What path prefix will every document of this application live under?",
                        "Immutable once documents exist, and matched as a literal prefix, so end it in a slash: north "
                        + "otherwise also matches northgate-archive.",
                        InterviewKinds.Line,
                        Required: true,
                        Default: null,
                        Choices: [],
                        MapsTo: "runbook:spec.sources.prefix"),
                    new(
                        "prefix.areas",
                        "List the folders under that prefix, one per governance boundary.",
                        "One collection per area. Boundaries follow governance, not topics: ask who must NOT see it.",
                        InterviewKinds.Areas,
                        Required: true,
                        Default: null,
                        Choices: [],
                        MapsTo: "runbook:spec.collections[*].sources.filenamePrefix"),
                ]),
            new(
                "access",
                "Levels and compartments",
                "dev-guide 16, Levels and compartments",
                [
                    new(
                        "access.uniform_public",
                        "Is the whole corpus one audience, as with public documents?",
                        "Uniform level 0 is honest for public corpora - accept the uniform-access Info finding rather "
                        + "than inventing a clearance story.",
                        InterviewKinds.Bool,
                        Required: false,
                        Default: true,
                        Choices: [],
                        MapsTo: "runbook:spec.collections[*].accessLevel"),
                    new(
                        "access.area_levels",
                        "Access level per area, 0 to 3.",
                        "Few levels only. Two same-seniority audiences that must not see each other are two "
                        + "compartments at one level, not two levels.",
                        InterviewKinds.Map,
                        Required: false,
                        Default: null,
                        Choices: [],
                        MapsTo: "runbook:spec.collections[*].accessLevel"),
                    new(
                        "access.area_compartments",
                        "Compartment tags per area, as need-to-know sets.",
                        "A data-sensitivity set rather than a team, and several on one collection mean AND - so an "
                        + "either-or set is two collections, not one.",
                        InterviewKinds.Map,
                        Required: false,
                        Default: null,
                        Choices: [],
                        MapsTo: "runbook:spec.collections[*].compartments"),
                ]),
            new(
                "retrieval",
                "Retrieval knobs and chunking",
                "dev-guide 16, The hybrid mechanics you inherit",
                [
                    new(
                        "retrieval.top_k",
                        "How many fused hits should a query return?",
                        "Query-time, so changing it is a new runbook version rather than a rebuild.",
                        InterviewKinds.Number,
                        Required: false,
                        Default: 10,
                        Choices: [],
                        MapsTo: "runbook:spec.retrieval.topK"),
                    new(
                        "retrieval.candidate_n",
                        "How many candidates should each retrieval leg contribute to fusion?",
                        "Wider corpora raise it so fusion has something to fuse.",
                        InterviewKinds.Number,
                        Required: false,
                        Default: 100,
                        Choices: [],
                        MapsTo: "runbook:spec.retrieval.candidateN"),
                    new(
                        "retrieval.rrf_k",
                        "Reciprocal-rank-fusion constant.",
                        "Leave it unless there is a measured reason to move it.",
                        InterviewKinds.Number,
                        Required: false,
                        Default: 60,
                        Choices: [],
                        MapsTo: "runbook:spec.retrieval.rrfK"),
                    new(
                        "retrieval.max_chars",
                        "Maximum characters per indexed chunk.",
                        "Lives in the SHAPE and is part of index identity, so changing it later is a rebuild. "
                        + "Committed corpora use 900 to 1500.",
                        InterviewKinds.Number,
                        Required: false,
                        Default: 1200,
                        Choices: [],
                        MapsTo: "shape:spec.chunking.max_chars"),
                    new(
                        "retrieval.embedding",
                        "Embedding source.",
                        "The free default is feature hashing rather than a model, so it will not match paraphrase; a "
                        + "BYOK embedding is a measured choice costing a rebuild.",
                        InterviewKinds.Enum,
                        Required: false,
                        Default: "keyless-default",
                        Choices: ["keyless-default", "byok"],
                        MapsTo: "runbook:spec.models.tasks.embedding"),
                ]),
            new(
                "extraction",
                "Media types and the fact vocabulary",
                "dev-guide 16, Extraction realities by corpus type",
                [
                    new(
                        "extraction.media_types",
                        "Media types per area, only where the corpus genuinely mixes formats.",
                        "Prefix and media type AND together, so an unneeded constraint binds nothing silently. After "
                        + "the first build, sweep extraction_status for empty.",
                        InterviewKinds.Map,
                        Required: false,
                        Default: null,
                        Choices: [],
                        MapsTo: "runbook:spec.collections[*].sources.mediaTypes"),
                    new(
                        "extraction.fact_fields",
                        "Extra fact-body fields beyond subject, key and value.",
                        "subject.key splits at the LAST dot, so keys carry no dots. Add fields only if extraction "
                        + "genuinely mints them.",
                        InterviewKinds.Fields,
                        Required: false,
                        Default: null,
                        Choices: [],
                        MapsTo: "shape:spec.fact.schema.properties"),
                ]),
            new(
                "lifecycle",
                "Index lifecycle",
                "dev-guide 16, The index lifecycle from the application seat",
                [
                    new(
                        "lifecycle.cutover_approval",
                        "Require a human approval before a rebuilt index goes live?",
                        "Every committed exemplar gates cutover: the side-by-side build is free to fail, and the "
                        + "approval is where a human decides the new index goes live.",
                        InterviewKinds.Bool,
                        Required: false,
                        Default: true,
                        Choices: [],
                        MapsTo: "runbook:spec.steps.cutover.approval"),
                    new(
                        "lifecycle.keep_versions",
                        "How many retired index versions to keep for rollback?",
                        "Zero reclaims immediately, and rollback then needs a rebuild.",
                        InterviewKinds.Number,
                        Required: false,
                        Default: 2,
                        Choices: [],
                        MapsTo: "runbook:spec.steps.retireOld.keep_versions"),
                ]),
        ];

        // The completion section follows the pattern's own arm. With no pattern chosen nothing has been ruled out, so it is
        // asked - which is why the section is appended rather than always present.
        if (pattern is null || pattern.HasCompletion)
        {
            sections.Add(new(
                "completion",
                "Completion, as RAG answering",
                "dev-guide 17, The completion path and the grounding lessons",
                [
                    new(
                        "completion.enabled",
                        "Should this application answer questions with a model?",
                        "If not, the runbook builds and serves the index only and your own orchestration calls search.",
                        InterviewKinds.Bool,
                        Required: false,
                        Default: true,
                        Choices: [],
                        MapsTo: "runbook:spec.completion"),
                    new(
                        "completion.tier",
                        "Model tier for completion.",
                        "Fast where measurement showed model-invariance, capable where nuance was measured to matter, "
                        + "and frontier only for runbooks whose evaluations earned it.",
                        InterviewKinds.Enum,
                        Required: false,
                        Default: "capable",
                        Choices: ["fast", "capable", "frontier"],
                        MapsTo: "runbook:spec.models.tasks.completion.tier"),
                    new(
                        "completion.verification_quotes",
                        "Verify that quoted spans resolve verbatim in served text?",
                        "The measured retry that cut quote failures threefold, before the answer stands.",
                        InterviewKinds.Bool,
                        Required: false,
                        Default: true,
                        Choices: [],
                        MapsTo: "runbook:spec.completion.verification.quotes"),
                    new(
                        "completion.verification_citations",
                        "Verify that every citation names content actually served?",
                        "A search hit you did not read is not a citation - the chat failure that drove four kernel "
                        + "changes.",
                        InterviewKinds.Bool,
                        Required: false,
                        Default: true,
                        Choices: [],
                        MapsTo: "runbook:spec.completion.verification.citations"),
                    new(
                        "completion.allow_overrides",
                        "May API callers override the completion model?",
                        "The override policy protects a published runbook's spend attribution: none is closed and the "
                        + "default, all permits any configured provider.",
                        InterviewKinds.Enum,
                        Required: false,
                        Default: "none",
                        Choices: ["none", "all"],
                        MapsTo: "runbook:spec.models.allowOverrides"),
                ]));
        }

        return sections;

    }
}
