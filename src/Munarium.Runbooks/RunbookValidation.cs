namespace Munarium.Runbooks;

/// <summary>
/// How much a finding matters.
/// </summary>
/// <remarks>
/// This is the runbook validator's own vocabulary and not the mesh findings': an <see cref="Error"/> here means the
/// document should not be applied as it stands, where the mesh's severities speak about a claim. Keeping them apart is
/// why the wire names differ, and why a report cannot accidentally read one as the other.
/// </remarks>
public enum Severity
{
    /// <summary>The document should not be applied or run as it stands.</summary>
    Error = 0,

    /// <summary>Legal, but probably not what its author wants.</summary>
    Warn = 1,

    /// <summary>Advisory.</summary>
    Info = 2,
}

/// <summary>
/// One thing a runbook got wrong, or should think about.
/// </summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Code">The stable dotted code, such as <c>steps.cutover-before-build</c>.</param>
/// <param name="Message">What a person reads.</param>
/// <param name="Path">Where it is, as a YAML-ish path such as <c>spec.collections[1].name</c>.</param>
public sealed record ValidationFinding(Severity Severity, string Code, string Message, string Path);

/// <summary>
/// Deterministic runbook validation: the pure structural and semantic checks a server runs with no model calls.
/// </summary>
/// <remarks>
/// Parsing already refuses a structurally broken document; this reports the semantic layer. It is deliberately
/// <em>findings</em> rather than refusals: an operator editing a runbook wants the whole list, and a document with a
/// questionable budget is still one they may be about to fix.
/// <para>
/// Every finding is a check that would otherwise fire mid-turn, in front of a user, with money already spent. The order
/// of the findings is the order of the checks, which is the order they are read in.
/// </para>
/// </remarks>
public static class RunbookValidation
{
    /// <summary>
    /// Checks a runbook.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The findings.</returns>
    public static List<ValidationFinding> Validate(RunbookDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var findings = new List<ValidationFinding>();
        var spec = document.Spec;

        ValidateMetadata(document, findings);
        ValidateSteps(spec, findings);
        ValidateCollections(spec, findings);
        ValidateSources(spec, findings);
        ValidateRetrieval(spec, findings);
        ValidateModels(spec, findings);
        ValidateCompletion(spec, findings);

        return findings;
    }

    /// <summary>
    /// Reports whether a document may be applied.
    /// </summary>
    /// <param name="findings">The findings.</param>
    /// <returns><see langword="true"/> when no finding is an error.</returns>
    public static bool IsValid(IReadOnlyList<ValidationFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return !findings.Any(finding => finding.Severity == Severity.Error);
    }

    private static void ValidateMetadata(RunbookDocument document, List<ValidationFinding> findings)
    {
        if (document.Metadata.Name.Trim().Length == 0)
        {
            findings.Add(new ValidationFinding(
                Severity.Error,
                "metadata.name-empty",
                "runbook name must be non-empty",
                "metadata.name"));
        }

        if (document.Metadata.Version == 0)
        {
            findings.Add(new ValidationFinding(
                Severity.Warn,
                "metadata.version-zero",
                "version 0 is legal but unconventional; versions usually start at 1",
                "metadata.version"));
        }

        // A configured embedding model is accepted and policy-checked but not yet consumed: index builds embed with the
        // built-in keyless embedder. Saying so here is the difference between an operator believing their embedding
        // provider is in the loop and knowing it is not.
        if (document.Spec.Models?.Tasks.TryGetValue(TaskLevels.Embedding, out var embedding) == true
            && embedding is { IsEmpty: false })
        {
            findings.Add(new ValidationFinding(
                Severity.Info,
                "models.embedding-not-consumed",
                "models.embedding is accepted and validated but not yet consumed - index builds use the built-in local "
                    + "embedder; provider-backed embeddings are a tracked follow-up",
                "spec.models.embedding"));
        }
    }

    /// <summary>
    /// Checks the step ordering, which is the one thing a run cannot recover from.
    /// </summary>
    /// <remarks>
    /// A dependent step before the build it depends on does not fail at apply time - it fails when the run reaches it,
    /// which is after the earlier steps have spent their work.
    /// </remarks>
    private static void ValidateSteps(RunbookSpec spec, List<ValidationFinding> findings)
    {
        var build = Position(spec, "buildIndex");

        if (build is null)
        {
            findings.Add(new ValidationFinding(
                Severity.Warn,
                "steps.no-build",
                "no buildIndex step - this runbook never (re)indexes anything",
                "spec.steps"));
        }

        foreach (var (dependent, code) in new[]
        {
            ("verify", "steps.verify-before-build"),
            ("cutover", "steps.cutover-before-build"),
            ("retireOld", "steps.retire-before-build"),
        })
        {
            if (Position(spec, dependent) is not { } at || build is not { } built || at >= built)
            {
                continue;
            }

            findings.Add(new ValidationFinding(
                Severity.Error,
                code,
                $"{dependent} requires a prior buildIndex step",
                $"spec.steps[{at}]"));
        }

        // Both positions are known here, which is why this reads as a pair rather than as another loop.
        if (Position(spec, "cutover") is { } cutover && Position(spec, "verify") is { } verification
            && cutover < verification)
        {
            findings.Add(new ValidationFinding(
                Severity.Warn,
                "steps.cutover-before-verify",
                "cutover precedes verify - the index goes live before verification",
                $"spec.steps[{cutover}]"));
        }

        if (Position(spec, "cutover") is { } unapproved && !spec.Steps[unapproved].RequiresApproval)
        {
            findings.Add(new ValidationFinding(
                Severity.Info,
                "steps.cutover-unapproved",
                "cutover has no approval gate; the index goes live without a human in the loop",
                $"spec.steps[{unapproved}]"));
        }

        for (var index = 0; index < spec.Steps.Count; index++)
        {
            if (spec.Steps[index] is { Kind: StepKind.RetireOld, KeepVersions: 0 })
            {
                findings.Add(new ValidationFinding(
                    Severity.Warn,
                    "steps.retire-keeps-none",
                    "keep_versions: 0 reclaims every inactive version's chunks immediately - rollback needs a rebuild",
                    $"spec.steps[{index}]"));
            }
        }
    }

    private static int? Position(RunbookSpec spec, string name)
    {
        for (var index = 0; index < spec.Steps.Count; index++)
        {
            if (string.Equals(spec.Steps[index].Name, name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return null;
    }

    private static void ValidateCollections(RunbookSpec spec, List<ValidationFinding> findings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < spec.Collections.Count; index++)
        {
            var path = $"spec.collections[{index}]";
            var collection = spec.Collections[index];

            if (collection.Name.Trim().Length == 0)
            {
                findings.Add(new ValidationFinding(
                    Severity.Error,
                    "collections.name-empty",
                    "collection name must be non-empty",
                    $"{path}.name"));
            }

            if (!seen.Add(collection.Name))
            {
                findings.Add(new ValidationFinding(
                    Severity.Error,
                    "collections.name-duplicate",
                    $"duplicate collection name '{collection.Name}'",
                    $"{path}.name"));
            }

            if (collection.AccessLevel < 0)
            {
                findings.Add(new ValidationFinding(
                    Severity.Error,
                    "collections.negative-level",
                    "access_level must be >= 0",
                    $"{path}.accessLevel"));
            }

            // An unpinned shape is the failure mode the whole pinning convention exists for: a shape that changed under
            // a runbook is a different question being answered.
            if (!collection.Shape.Contains('@', StringComparison.Ordinal))
            {
                findings.Add(new ValidationFinding(
                    Severity.Warn,
                    "collections.shape-unversioned",
                    $"shape '{collection.Shape}' has no @version - pin the shape version",
                    $"{path}.shape"));
            }

            CollectionBinding(collection, path, findings);
        }

        // Identical (level, compartments) across every collection means the compartmentalization does nothing, which is
        // worth saying out loud for a runbook that went to the trouble of declaring it.
        if (spec.Collections.Count > 1
            && spec.Collections
                .Select(collection => (collection.AccessLevel, Key: CompartmentsKey(collection.Compartments)))
                .Distinct()
                .Count() == 1)
        {
            findings.Add(new ValidationFinding(
                Severity.Info,
                "collections.uniform-access",
                "every collection has the same access level and compartments - one runbook serving multiple clearance "
                    + "levels usually wants them to differ",
                "spec.collections"));
        }
    }

    private static void CollectionBinding(
        CollectionSpec collection,
        string path,
        List<ValidationFinding> findings)
    {
        if (collection.Sources is null)
        {
            findings.Add(new ValidationFinding(
                Severity.Info,
                "collections.no-source-binding",
                $"collection '{collection.Name}' declares no source binding - sources must be bound explicitly at ingest",
                $"{path}.sources"));
            return;
        }

        if (collection.Sources.IsEmpty)
        {
            findings.Add(new ValidationFinding(
                Severity.Warn,
                "collections.empty-source-binding",
                "sources: {} matches nothing; drop it or add a matcher",
                $"{path}.sources"));
            return;
        }

        for (var index = 0; index < collection.Sources.MediaTypes.Count; index++)
        {
            var mediaType = collection.Sources.MediaTypes[index];

            if (!mediaType.Contains('/', StringComparison.Ordinal))
            {
                findings.Add(new ValidationFinding(
                    Severity.Warn,
                    "collections.bad-media-type",
                    $"'{mediaType}' does not look like a media type (type/subtype)",
                    $"{path}.sources.mediaTypes[{index}]"));
            }
        }
    }

    private static string CompartmentsKey(IReadOnlyList<string> compartments) =>
        string.Join('\u001f', compartments.Order(StringComparer.Ordinal));

    private static void ValidateSources(RunbookSpec spec, List<ValidationFinding> findings)
    {
        if (spec.Sources is not { Prefix: { Length: > 0 } prefix })
        {
            return;
        }

        if (prefix.StartsWith('/') || prefix.Contains("..", StringComparison.Ordinal)
            || prefix.Contains('\\', StringComparison.Ordinal))
        {
            findings.Add(new ValidationFinding(
                Severity.Error,
                "sources.prefix-invalid",
                $"prefix '{prefix}' is not a valid blob path prefix (no leading '/', '..', or backslashes)",
                "spec.sources.prefix"));
        }
        else if (!prefix.EndsWith('/'))
        {
            // Matching is a literal starts_with, so "north" also matches "northgate-archive/" - almost never the intent.
            findings.Add(new ValidationFinding(
                Severity.Warn,
                "sources.prefix-unterminated",
                $"prefix '{prefix}' does not end in '/'; matching is a literal starts_with, so it also matches sibling "
                    + "paths that merely begin with those characters",
                "spec.sources.prefix"));
        }

        // Every collection has to actually sit under the declared prefix, or the runbook claims its documents live
        // somewhere its own bindings can never match.
        for (var index = 0; index < spec.Collections.Count; index++)
        {
            if (spec.Collections[index] is not { Sources.FilenamePrefix: { Length: > 0 } bound })
            {
                continue;
            }

            if (!bound.StartsWith(prefix, StringComparison.Ordinal))
            {
                findings.Add(new ValidationFinding(
                    Severity.Error,
                    "sources.prefix-mismatch",
                    $"collection '{spec.Collections[index].Name}' binds '{bound}', which is not under the declared "
                        + $"sources prefix '{prefix}' - this runbook can never match it",
                    $"spec.collections[{index}].sources.filenamePrefix"));
            }
        }

        if (spec.Sources.Container is null)
        {
            findings.Add(new ValidationFinding(
                Severity.Info,
                "sources.container-unset",
                "no container declared; the server's configured default applies",
                "spec.sources.container"));
        }
    }

    /// <summary>
    /// Checks the retrieval knobs.
    /// </summary>
    /// <remarks>
    /// Every range here is a value the engine would otherwise accept and behave badly on: a concurrency above the pool
    /// size waits, a candidate count below the requested hits cannot fill them, and a stop-term fraction outside its
    /// band either does nothing or collapses the candidate set.
    /// </remarks>
    private static void ValidateRetrieval(RunbookSpec spec, List<ValidationFinding> findings)
    {
        if (spec.Retrieval is not { } retrieval)
        {
            return;
        }

        const string Path = "spec.retrieval";

        if (retrieval.TopK == 0 || retrieval.TopK > 100)
        {
            findings.Add(Error("retrieval.top-k-range", $"topK {retrieval.TopK} outside 1..=100", $"{Path}.topK"));
        }

        if (retrieval.RrfK is < 1.0 or > 1000.0 || !double.IsFinite(retrieval.RrfK))
        {
            findings.Add(Error("retrieval.rrf-k-range", $"rrfK {retrieval.RrfK} outside 1..=1000", $"{Path}.rrfK"));
        }

        if (retrieval.CandidateN is < 1 or > 1000)
        {
            findings.Add(Error(
                "retrieval.candidate-n-range",
                $"candidateN {retrieval.CandidateN} outside 1..=1000",
                $"{Path}.candidateN"));
        }

        if (retrieval.CandidateN < retrieval.TopK)
        {
            findings.Add(new ValidationFinding(
                Severity.Warn,
                "retrieval.candidates-below-top-k",
                "candidateN < topK - fusion can never fill topK hits",
                Path));
        }

        if (!double.IsFinite(retrieval.QueryExpansionWeight) || retrieval.QueryExpansionWeight is < 0.0 or > 1.0)
        {
            findings.Add(Error(
                "retrieval.query-expansion-weight-range",
                "queryExpansionWeight must be finite and in 0..=1",
                $"{Path}.queryExpansionWeight"));
        }

        if (!double.IsFinite(retrieval.StopTermFraction)
            || (retrieval.StopTermFraction != 0.0 && retrieval.StopTermFraction is < 0.05 or > 0.9))
        {
            findings.Add(Error(
                "retrieval.stop-term-fraction-range",
                "stopTermFraction must be 0 (off) or in 0.05..=0.9",
                $"{Path}.stopTermFraction"));
        }

        if (retrieval.MinimumShouldMatch is < 1 or > 2)
        {
            findings.Add(Error(
                "retrieval.minimum-should-match-range",
                "minimumShouldMatch must be 1 (any query word) or 2 (at least two)",
                $"{Path}.minimumShouldMatch"));
        }

        if (retrieval.SearchConcurrency is < 1 or > 16)
        {
            findings.Add(Error(
                "retrieval.search-concurrency-range",
                "searchConcurrency must be in 1..=16 (each in-flight search holds a pooled connection)",
                $"{Path}.searchConcurrency"));
        }

        ValidateCollectionSelection(retrieval, findings);
        ValidateFusion(retrieval, findings);
        ValidateModelExpansion(retrieval, findings);
        ValidateRoutes(spec, retrieval, findings);
        ValidateExpansions(retrieval, findings);
        ValidateDemotions(spec, retrieval, findings);
    }

    private static void ValidateCollectionSelection(RetrievalSpec retrieval, List<ValidationFinding> findings)
    {
        if (retrieval.CollectionSelection is not { } selection)
        {
            return;
        }

        const string Path = "spec.retrieval.collectionSelection";

        if (!double.IsFinite(selection.PhraseBoost) || selection.PhraseBoost is < 0.0 or > 10.0)
        {
            findings.Add(Error(
                "retrieval.collection-selection-phrase-boost-range",
                "phraseBoost must be in 0..=10",
                $"{Path}.phraseBoost"));
        }

        if (selection.MaxCollections is < 1 or > 64)
        {
            findings.Add(Error(
                "retrieval.collection-selection-max-collections-range",
                "maxCollections must be in 1..=64",
                $"{Path}.maxCollections"));
        }

        if (selection.ProbeCandidateN is < 1 or > 1000)
        {
            findings.Add(Error(
                "retrieval.collection-selection-probe-candidates-range",
                "probeCandidateN must be in 1..=1000",
                $"{Path}.probeCandidateN"));
        }

        if (selection.CandidatePoolPerCollection is < 1 or > 1000)
        {
            findings.Add(Error(
                "retrieval.collection-selection-candidate-pool-range",
                "candidatePoolPerCollection must be in 1..=1000",
                $"{Path}.candidatePoolPerCollection"));
        }
        else if (selection.CandidatePoolPerCollection < retrieval.TopK)
        {
            findings.Add(new ValidationFinding(
                Severity.Warn,
                "retrieval.collection-selection-pool-below-top-k",
                "candidatePoolPerCollection < topK can discard useful per-collection candidates before global fusion",
                Path));
        }
    }

    private static void ValidateFusion(RetrievalSpec retrieval, List<ValidationFinding> findings)
    {
        if (retrieval.Fusion is not { } fusion)
        {
            return;
        }

        const string Path = "spec.retrieval.fusion";

        foreach (var (field, value) in new[]
        {
            ("lexicalWeight", fusion.LexicalWeight),
            ("vectorWeight", fusion.VectorWeight),
            ("collectionEvidenceWeight", fusion.CollectionEvidenceWeight),
            ("unselectedPoolWeight", fusion.UnselectedPoolWeight),
        })
        {
            if (!double.IsFinite(value) || value is < 0.0 or > 10.0)
            {
                findings.Add(Error("retrieval.fusion-weight-range", $"{field} must be in 0..=10", $"{Path}.{field}"));
            }
        }

        if (fusion.LexicalWeight == 0.0 && fusion.VectorWeight == 0.0)
        {
            findings.Add(Error(
                "retrieval.fusion-no-leg",
                "lexicalWeight and vectorWeight cannot both be 0 - nothing would rank the hits",
                Path));
        }

        // The third leg reads the selection's ranking, so without a selection there is nothing for it to read.
        if (fusion.CollectionEvidenceWeight > 0.0 && retrieval.CollectionSelection is null)
        {
            findings.Add(new ValidationFinding(
                Severity.Warn,
                "retrieval.fusion-evidence-without-selection",
                "collectionEvidenceWeight has no effect without collectionSelection (no evidence ranking is produced)",
                $"{Path}.collectionEvidenceWeight"));
        }
    }

    private static void ValidateModelExpansion(RetrievalSpec retrieval, List<ValidationFinding> findings)
    {
        if (retrieval.ModelQueryExpansion is not { } expansion)
        {
            return;
        }

        const string Path = "spec.retrieval.modelQueryExpansion";

        if (expansion.MaxTerms is < 1 or > 32)
        {
            findings.Add(Error(
                "retrieval.model-query-expansion-max-terms-range",
                "maxTerms must be in 1..=32",
                $"{Path}.maxTerms"));
        }

        if (expansion.MaxTokens is { } tokens && tokens is < 32 or > 512)
        {
            findings.Add(Error(
                "retrieval.model-query-expansion-max-tokens-range",
                "maxTokens must be in 32..=512",
                $"{Path}.maxTokens"));
        }
    }

    /// <summary>
    /// Checks the collection routes: a route that names a collection this runbook does not bind can never apply.
    /// </summary>
    private static void ValidateRoutes(RunbookSpec spec, RetrievalSpec retrieval, List<ValidationFinding> findings)
    {
        const string Path = "spec.retrieval";

        if (retrieval.CollectionRoutes.Count > 32)
        {
            findings.Add(Error(
                "retrieval.too-many-collection-routes",
                $"{retrieval.CollectionRoutes.Count} collectionRoutes exceeds the maximum of 32",
                $"{Path}.collectionRoutes"));
        }

        var declared = new HashSet<string>(
            spec.Collections.Select(collection => collection.Name),
            StringComparer.Ordinal);

        for (var index = 0; index < retrieval.CollectionRoutes.Count; index++)
        {
            var route = retrieval.CollectionRoutes[index];
            var path = $"{Path}.collectionRoutes[{index}]";

            if (route.WhenAll.Count == 0)
            {
                findings.Add(Error(
                    "retrieval.collection-route-no-trigger",
                    "whenAll must contain at least one trigger term",
                    $"{path}.whenAll"));
            }

            if (route.Collections.Count == 0)
            {
                findings.Add(Error(
                    "retrieval.collection-route-no-collections",
                    "collections must contain at least one runbook collection",
                    $"{path}.collections"));
            }

            for (var term = 0; term < route.WhenAll.Count; term++)
            {
                if (route.WhenAll[term].Trim().Length == 0 || route.WhenAll[term].Length > 80)
                {
                    findings.Add(Error(
                        "retrieval.collection-route-bad-term",
                        "terms must be non-empty and at most 80 bytes",
                        $"{path}.whenAll[{term}]"));
                }
            }

            for (var collection = 0; collection < route.Collections.Count; collection++)
            {
                if (!declared.Contains(route.Collections[collection]))
                {
                    findings.Add(Error(
                        "retrieval.collection-route-unknown-collection",
                        $"collection '{route.Collections[collection]}' is not bound by this runbook",
                        $"{path}.collections[{collection}]"));
                }
            }
        }
    }

    /// <summary>
    /// Checks the conditional vocabulary: an expansion that adds nothing, or triggers on nothing, is dead weight.
    /// </summary>
    private static void ValidateExpansions(RetrievalSpec retrieval, List<ValidationFinding> findings)
    {
        const string Path = "spec.retrieval";

        if (retrieval.QueryExpansions.Count > 32)
        {
            findings.Add(Error(
                "retrieval.too-many-query-expansions",
                $"{retrieval.QueryExpansions.Count} queryExpansions exceeds the maximum of 32",
                $"{Path}.queryExpansions"));
        }

        for (var index = 0; index < retrieval.QueryExpansions.Count; index++)
        {
            var rule = retrieval.QueryExpansions[index];
            var path = $"{Path}.queryExpansions[{index}]";

            if (rule.WhenAny.Count == 0)
            {
                findings.Add(Error(
                    "retrieval.query-expansion-no-trigger",
                    "whenAny must contain at least one trigger term",
                    $"{path}.whenAny"));
            }

            if (rule.AddTerms.Count == 0)
            {
                findings.Add(Error(
                    "retrieval.query-expansion-no-additions",
                    "addTerms must contain at least one term",
                    $"{path}.addTerms"));
            }

            foreach (var (field, terms) in new[]
            {
                ("whenAny", rule.WhenAny),
                ("addTerms", rule.AddTerms),
            })
            {
                if (terms.Count > 64)
                {
                    findings.Add(Error(
                        "retrieval.query-expansion-too-many-terms",
                        $"{field} exceeds the maximum of 64 terms",
                        $"{path}.{field}"));
                }

                for (var term = 0; term < terms.Count; term++)
                {
                    if (terms[term].Trim().Length == 0 || terms[term].Length > 80)
                    {
                        findings.Add(Error(
                            "retrieval.query-expansion-bad-term",
                            "terms must be non-empty and at most 80 bytes",
                            $"{path}.{field}[{term}]"));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks the content markers: one with no effect, or one that excepts every collection, says nothing.
    /// </summary>
    private static void ValidateDemotions(RunbookSpec spec, RetrievalSpec retrieval, List<ValidationFinding> findings)
    {
        const string Path = "spec.retrieval";

        if (retrieval.ContentDemotions.Count > 32)
        {
            findings.Add(Error(
                "retrieval.too-many-content-demotions",
                $"{retrieval.ContentDemotions.Count} contentDemotions exceeds the maximum of 32",
                $"{Path}.contentDemotions"));
        }

        var declared = new HashSet<string>(
            spec.Collections.Select(collection => collection.Name),
            StringComparer.Ordinal);

        for (var index = 0; index < retrieval.ContentDemotions.Count; index++)
        {
            var rule = retrieval.ContentDemotions[index];
            var path = $"{Path}.contentDemotions[{index}]";

            if (rule.Contains.Trim().Length == 0 || rule.Contains.Length > 512)
            {
                findings.Add(Error(
                    "retrieval.content-demotion-bad-marker",
                    "contains must be non-empty and at most 512 bytes",
                    $"{path}.contains"));
            }

            if (!double.IsFinite(rule.LexicalMultiplier) || rule.LexicalMultiplier is < 0.0 or > 1.0)
            {
                findings.Add(Error(
                    "retrieval.content-demotion-bad-lexical-multiplier",
                    "lexicalMultiplier must be finite and in 0..=1",
                    $"{path}.lexicalMultiplier"));
            }

            if (!double.IsFinite(rule.VectorDistancePenalty) || rule.VectorDistancePenalty is < 0.0 or > 10.0)
            {
                findings.Add(Error(
                    "retrieval.content-demotion-bad-vector-penalty",
                    "vectorDistancePenalty must be finite and in 0..=10",
                    $"{path}.vectorDistancePenalty"));
            }

            for (var name = 0; name < rule.ExceptCollections.Count; name++)
            {
                if (!declared.Contains(rule.ExceptCollections[name]))
                {
                    findings.Add(Error(
                        "retrieval.content-demotion-unknown-collection",
                        $"exceptCollections names '{rule.ExceptCollections[name]}', which spec.collections does not "
                            + "declare",
                        $"{path}.exceptCollections[{name}]"));
                }
            }

            if (rule.ExceptCollections.Count > 0 && rule.ExceptCollections.Count >= spec.Collections.Count)
            {
                findings.Add(new ValidationFinding(
                    Severity.Warn,
                    "retrieval.content-demotion-excepts-all",
                    "exceptCollections covers every collection - the rule never applies",
                    $"{path}.exceptCollections"));
            }

            // The document writes literal defaults, so this is an exact comparison on purpose: an operator who writes
            // 1.0 means exactly the default, and an epsilon would call 0.9999999999 the same thing - which is not what
            // a configuration file says.
#pragma warning disable S1244
            if (rule.LexicalMultiplier == 1.0 && rule.VectorDistancePenalty == 0.0)
#pragma warning restore S1244
            {
                findings.Add(new ValidationFinding(
                    Severity.Warn,
                    "retrieval.content-demotion-no-op",
                    "content demotion has no effect",
                    path));
            }
        }
    }

    /// <summary>
    /// Checks the model defaults.
    /// </summary>
    /// <remarks>
    /// An unknown task level or a tier outside the vocabulary is a typo that would otherwise resolve to the tenant's
    /// fallback chain, look like a document that said nothing, and be found by nobody.
    /// </remarks>
    private static void ValidateModels(RunbookSpec spec, List<ValidationFinding> findings)
    {
        if (spec.Models is not { } models)
        {
            return;
        }

        foreach (var (task, pinned) in models.Tasks)
        {
            var path = $"spec.models.tasks.{task}";

            if (!TaskLevels.All.Contains(task, StringComparer.Ordinal))
            {
                findings.Add(Error(
                    "models.unknown-task",
                    $"unknown task level '{task}' (known: {string.Join(", ", TaskLevels.All)})",
                    path));
            }

            if (pinned.IsEmpty)
            {
                findings.Add(Error("models.empty-spec", "a model spec needs provider, model, or tier", path));
            }

            if (pinned.Tier is { } tier && tier is not ("fast" or "capable" or "frontier"))
            {
                findings.Add(Error("models.bad-tier", $"tier '{tier}' must be fast|capable|frontier", $"{path}.tier"));
            }
        }

        if (models.Default is { IsEmpty: true })
        {
            findings.Add(Error(
                "models.empty-spec",
                "models.default needs provider, model, or tier",
                "spec.models.default"));
        }
    }

    private static void ValidateCompletion(RunbookSpec spec, List<ValidationFinding> findings)
    {
        if (spec.Completion is not { } completion)
        {
            return;
        }

        // A prompt that never interpolates the served context answers from what the model already believed, which is the
        // one thing a runbook like this exists to avoid.
        foreach (var placeholder in ((string[])["{context}", "{query}"]).Where(placeholder =>
            !completion.PromptTemplate.Contains(placeholder, StringComparison.Ordinal)))
        {
            findings.Add(new ValidationFinding(
                Severity.Warn,
                "completion.template-missing-var",
                $"promptTemplate does not reference {placeholder}",
                "spec.completion.promptTemplate"));
        }

        var hasModel = spec.Models is { } models
            && (models.Tasks.ContainsKey(TaskLevels.Completion) || models.Default is not null);

        if (!hasModel)
        {
            findings.Add(new ValidationFinding(
                Severity.Info,
                "completion.no-model-default",
                "no completion model default - turns will fall back to the tenant's default provider chain",
                "spec.models"));
        }

        if (completion.ContextCharBudget is { } budget && budget is < 4_000 or > 400_000)
        {
            findings.Add(Error(
                "completion.context-char-budget-range",
                "contextCharBudget must be in 4000..=400000 characters",
                "spec.completion.contextCharBudget"));
        }

        if (completion.MaxTokens is { } tokens && tokens is < 256 or > 16_384)
        {
            findings.Add(Error(
                "completion.max-tokens-range",
                "maxTokens must be in 256..=16384 (the truncation retry pays 4x this)",
                "spec.completion.maxTokens"));
        }
    }

    private static ValidationFinding Error(string code, string message, string path) =>
        new(Severity.Error, code, message, path);
}
