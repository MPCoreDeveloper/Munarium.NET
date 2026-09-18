namespace Munarium.Runbooks;

using Munarium.Evidence;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Reads the retrieval half of a runbook.
/// </summary>
/// <remarks>
/// The specs here deny unknown fields, exactly as the original does: these are the knobs where a misspelled key would
/// silently leave the engine at its default and look like a document that said nothing.
/// </remarks>
internal static class RunbookRetrievalReader
{
    internal static RetrievalSpec Read(YamlMappingNode retrieval)
    {
        RunbookYaml.DenyUnknown(
            retrieval,
            "spec.retrieval",
            "topK",
            "rrfK",
            "candidateN",
            "searchConcurrency",
            "minimumShouldMatch",
            "stopTermFraction",
            "researchProfiles",
            "defaultResearchProfile",
            "queryExpansions",
            "queryExpansionWeight",
            "modelQueryExpansion",
            "collectionSelection",
            "fusion",
            "collectionRoutes",
            "contentDemotions");

        var defaults = new RetrievalSpec();

        return new RetrievalSpec
        {
            TopK = (int)(RunbookYaml.Integer(retrieval, "topK", "spec.retrieval.topK") ?? defaults.TopK),
            RrfK = RunbookYaml.Number(retrieval, "rrfK", "spec.retrieval.rrfK") ?? defaults.RrfK,
            CandidateN = RunbookYaml.Integer(retrieval, "candidateN", "spec.retrieval.candidateN")
                ?? defaults.CandidateN,
            SearchConcurrency = (int)(RunbookYaml.Integer(
                retrieval, "searchConcurrency", "spec.retrieval.searchConcurrency") ?? defaults.SearchConcurrency),
            MinimumShouldMatch = (int)(RunbookYaml.Integer(
                retrieval, "minimumShouldMatch", "spec.retrieval.minimumShouldMatch") ?? defaults.MinimumShouldMatch),
            StopTermFraction = RunbookYaml.Number(
                retrieval, "stopTermFraction", "spec.retrieval.stopTermFraction") ?? defaults.StopTermFraction,
            QueryExpansionWeight = RunbookYaml.Number(
                retrieval, "queryExpansionWeight", "spec.retrieval.queryExpansionWeight")
                ?? defaults.QueryExpansionWeight,
            ResearchProfiles =
            [
                .. RunbookYaml.Items(retrieval, "researchProfiles", "spec.retrieval.researchProfiles")
                    .Select((item, index) => ProfileOf(
                        RunbookYaml.AsMapping(item, $"spec.retrieval.researchProfiles[{index}]"),
                        $"spec.retrieval.researchProfiles[{index}]")),
            ],
            DefaultResearchProfile = RunbookYaml.OptionalText(
                retrieval, "defaultResearchProfile", "spec.retrieval.defaultResearchProfile"),
            QueryExpansions =
            [
                .. RunbookYaml.Items(retrieval, "queryExpansions", "spec.retrieval.queryExpansions")
                    .Select((item, index) => ExpansionOf(
                        RunbookYaml.AsMapping(item, $"spec.retrieval.queryExpansions[{index}]"),
                        $"spec.retrieval.queryExpansions[{index}]")),
            ],
            ModelQueryExpansion = RunbookYaml.Map(
                retrieval, "modelQueryExpansion", "spec.retrieval.modelQueryExpansion") is { } expansion
                ? ModelExpansionOf(expansion)
                : null,
            CollectionSelection = RunbookYaml.Map(
                retrieval, "collectionSelection", "spec.retrieval.collectionSelection") is { } selection
                ? SelectionOf(selection)
                : null,
            Fusion = RunbookYaml.Map(retrieval, "fusion", "spec.retrieval.fusion") is { } fusion
                ? FusionOf(fusion)
                : null,
            CollectionRoutes =
            [
                .. RunbookYaml.Items(retrieval, "collectionRoutes", "spec.retrieval.collectionRoutes")
                    .Select((item, index) => RouteOf(
                        RunbookYaml.AsMapping(item, $"spec.retrieval.collectionRoutes[{index}]"),
                        $"spec.retrieval.collectionRoutes[{index}]")),
            ],
            ContentDemotions =
            [
                .. RunbookYaml.Items(retrieval, "contentDemotions", "spec.retrieval.contentDemotions")
                    .Select((item, index) => DemotionOf(
                        RunbookYaml.AsMapping(item, $"spec.retrieval.contentDemotions[{index}]"),
                        $"spec.retrieval.contentDemotions[{index}]")),
            ],
        };
    }

    private static QueryExpansionSpec ExpansionOf(YamlMappingNode mapping, string path)
    {
        RunbookYaml.DenyUnknown(mapping, path, "whenAny", "addTerms");

        return new QueryExpansionSpec
        {
            WhenAny = RunbookYaml.Strings(mapping, "whenAny", $"{path}.whenAny"),
            AddTerms = RunbookYaml.Strings(mapping, "addTerms", $"{path}.addTerms"),
        };
    }

    private static ModelQueryExpansionSpec ModelExpansionOf(YamlMappingNode mapping)
    {
        const string Path = "spec.retrieval.modelQueryExpansion";

        RunbookYaml.DenyUnknown(mapping, Path, "maxTerms", "maxTokens", "required");

        var defaults = new ModelQueryExpansionSpec();

        return new ModelQueryExpansionSpec
        {
            MaxTerms = (int)(RunbookYaml.Integer(mapping, "maxTerms", $"{Path}.maxTerms") ?? defaults.MaxTerms),

            // Absent means the server's configured budget for this task, not a constant of the grammar.
            MaxTokens = RunbookYaml.Integer(mapping, "maxTokens", $"{Path}.maxTokens") is { } tokens
                ? (int)tokens
                : null,
            Required = RunbookYaml.Boolean(mapping, "required", $"{Path}.required") ?? defaults.Required,
        };
    }

    private static CollectionSelectionSpec SelectionOf(YamlMappingNode mapping)
    {
        const string Path = "spec.retrieval.collectionSelection";

        RunbookYaml.DenyUnknown(
            mapping,
            Path,
            "maxCollections",
            "probeCandidateN",
            "candidatePoolPerCollection",
            "phraseBoost");

        var defaults = new CollectionSelectionSpec { MaxCollections = 0 };
        var maxCollections = RunbookYaml.Integer(mapping, "maxCollections", $"{Path}.maxCollections")
            ?? throw new FormatException($"{Path}.maxCollections is required");

        return new CollectionSelectionSpec
        {
            MaxCollections = (int)maxCollections,
            ProbeCandidateN = RunbookYaml.Integer(mapping, "probeCandidateN", $"{Path}.probeCandidateN")
                ?? defaults.ProbeCandidateN,
            CandidatePoolPerCollection = (int)(RunbookYaml.Integer(
                mapping, "candidatePoolPerCollection", $"{Path}.candidatePoolPerCollection")
                ?? defaults.CandidatePoolPerCollection),
            PhraseBoost = RunbookYaml.Number(mapping, "phraseBoost", $"{Path}.phraseBoost") ?? defaults.PhraseBoost,
        };
    }

    private static FusionSpec FusionOf(YamlMappingNode mapping)
    {
        const string Path = "spec.retrieval.fusion";

        RunbookYaml.DenyUnknown(
            mapping,
            Path,
            "lexicalWeight",
            "vectorWeight",
            "collectionEvidenceWeight",
            "unselectedPoolWeight");

        var defaults = new FusionSpec();

        return new FusionSpec
        {
            LexicalWeight = RunbookYaml.Number(mapping, "lexicalWeight", $"{Path}.lexicalWeight")
                ?? defaults.LexicalWeight,
            VectorWeight = RunbookYaml.Number(mapping, "vectorWeight", $"{Path}.vectorWeight")
                ?? defaults.VectorWeight,
            CollectionEvidenceWeight = RunbookYaml.Number(
                mapping, "collectionEvidenceWeight", $"{Path}.collectionEvidenceWeight")
                ?? defaults.CollectionEvidenceWeight,
            UnselectedPoolWeight = RunbookYaml.Number(
                mapping, "unselectedPoolWeight", $"{Path}.unselectedPoolWeight")
                ?? defaults.UnselectedPoolWeight,
        };
    }

    private static CollectionRouteSpec RouteOf(YamlMappingNode mapping, string path)
    {
        RunbookYaml.DenyUnknown(mapping, path, "whenAll", "collections");

        return new CollectionRouteSpec
        {
            WhenAll = RunbookYaml.Strings(mapping, "whenAll", $"{path}.whenAll"),
            Collections = RunbookYaml.Strings(mapping, "collections", $"{path}.collections"),
        };
    }

    private static ContentDemotionSpec DemotionOf(YamlMappingNode mapping, string path)
    {
        RunbookYaml.DenyUnknown(
            mapping,
            path,
            "contains",
            "lexicalMultiplier",
            "vectorDistancePenalty",
            "exceptCollections",
            "match");

        var defaults = new ContentDemotionSpec { Contains = string.Empty };
        var match = RunbookYaml.OptionalText(mapping, "match", $"{path}.match");

        return new ContentDemotionSpec
        {
            Contains = RunbookYaml.OptionalText(mapping, "contains", $"{path}.contains")
                ?? throw new FormatException($"{path}.contains is required"),
            LexicalMultiplier = RunbookYaml.Number(mapping, "lexicalMultiplier", $"{path}.lexicalMultiplier")
                ?? defaults.LexicalMultiplier,
            VectorDistancePenalty = RunbookYaml.Number(
                mapping, "vectorDistancePenalty", $"{path}.vectorDistancePenalty")
                ?? defaults.VectorDistancePenalty,
            ExceptCollections = RunbookYaml.Strings(mapping, "exceptCollections", $"{path}.exceptCollections"),
            Match = match switch
            {
                null or "substring" => DemotionMatch.Substring,
                "phrase" => DemotionMatch.Phrase,
                _ => throw new FormatException($"{path}.match must be 'substring' or 'phrase', got '{match}'"),
            },
        };
    }

    /// <summary>
    /// Reads a research profile.
    /// </summary>
    /// <remarks>
    /// The research specs are open, exactly as they are in the original: a profile is a place a deployment may carry its
    /// own notes without the reader refusing the document. What is closed is what the profile <em>means</em>, and
    /// <see cref="ResearchValidation"/> is what says so.
    /// </remarks>
    private static ResearchProfile ProfileOf(YamlMappingNode mapping, string path)
    {
        return new ResearchProfile
        {
            Name = RunbookYaml.OptionalText(mapping, "name", $"{path}.name")
                ?? throw new FormatException($"{path}.name is required"),
            Description = RunbookYaml.OptionalText(mapping, "description", $"{path}.description"),
            ContextCharBudget = Budget(mapping, "contextCharBudget", path),
            Layers =
            [
                .. RunbookYaml.Items(mapping, "layers", $"{path}.layers")
                    .Select((item, index) => LayerOf(
                        RunbookYaml.AsMapping(item, $"{path}.layers[{index}]"),
                        $"{path}.layers[{index}]")),
            ],
        };
    }

    private static ResearchLayer LayerOf(YamlMappingNode mapping, string path)
    {
        var requirement = RunbookYaml.OptionalText(mapping, "requirement", $"{path}.requirement");
        var role = RunbookYaml.OptionalText(mapping, "role", $"{path}.role");

        return new ResearchLayer
        {
            Name = RunbookYaml.OptionalText(mapping, "name", $"{path}.name")
                ?? throw new FormatException($"{path}.name is required"),

            // Pinned sources: a collection name, a fact version, a scope prefix, or a declared data view. What each one
            // has to be is checked when the profile is applied, not here.
            Sources = RunbookYaml.Strings(mapping, "sources", $"{path}.sources"),

            // Both vocabularies are closed, and a value outside them is refused rather than rounded to a default: a
            // layer whose role nobody recognized is a layer whose evidence nobody agreed on the weight of.
            Requirement = requirement is null
                ? LayerRequirement.Optional
                : HierarchyNames.ParseRequirement(requirement)
                    ?? throw new FormatException(
                        $"{path}.requirement must be 'required', 'optional' or 'fallback', got '{requirement}'"),
            Role = role is null
                ? AnswerRole.Primary
                : HierarchyNames.ParseRole(role)
                    ?? throw new FormatException(
                        $"{path}.role must be 'supporting', 'primary' or 'controlling', got '{role}'"),
            ContextCharBudget = Budget(mapping, "contextCharBudget", path),
            PreserveCompleteResult = RunbookYaml.Boolean(
                mapping, "preserveCompleteResult", $"{path}.preserveCompleteResult") ?? false,
            MaxBytes = RunbookYaml.Integer(mapping, "maxBytes", $"{path}.maxBytes"),

            // `deadline_ms` in the original and `deadlineMs` on the wire, which is what a document is written in.
            DeadlineMilliseconds = RunbookYaml.Integer(mapping, "deadlineMs", $"{path}.deadlineMs"),
        };
    }

    private static int? Budget(YamlMappingNode mapping, string name, string path) =>
        RunbookYaml.Integer(mapping, name, $"{path}.{name}") is { } budget ? (int)budget : null;
}
