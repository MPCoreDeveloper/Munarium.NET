namespace Munarium.Runbooks;

using Munarium.Evidence;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Reads a runbook's body: what it reads, how it retrieves, and how it runs.
/// </summary>
/// <remarks>
/// The rules that cross a spec live here, because they are the ones a document can satisfy field by field and still be
/// unusable: a document with no steps runs nothing, one that declares both a shape and collections does not say which
/// path it takes, and a semantic data view with no <c>intent</c> task can only ever refuse at the layer.
/// </remarks>
internal static class RunbookSpecs
{
    internal static RunbookSpec Read(YamlMappingNode spec)
    {
        var parsed = new RunbookSpec
        {
            Shape = Text(spec, "shape", "spec.shape"),
            Collections =
            [
                .. RunbookYaml.Items(spec, "collections", "spec.collections")
                    .Select((item, index) => CollectionOf(
                        RunbookYaml.AsMapping(item, $"spec.collections[{index}]"),
                        $"spec.collections[{index}]")),
            ],
            Sources = SourceLocationOf(spec),
            Execution = RunbookYaml.Map(spec, "execution", "spec.execution") is { } execution
                ? new ExecutionSpec { Order = ExecutionOrderOf(execution) }
                : null,
            DataViews =
            [
                .. RunbookYaml.Items(spec, "dataViews", "spec.dataViews")
                    .Select((item, index) => DataViewOf(
                        RunbookYaml.AsMapping(item, $"spec.dataViews[{index}]"),
                        $"spec.dataViews[{index}]")),
            ],
            Models = ModelsOf(spec),
            Completion = RunbookYaml.Map(spec, "completion", "spec.completion") is { } completion
                ? CompletionOf(completion)
                : null,
            Retrieval = RunbookYaml.Map(spec, "retrieval", "spec.retrieval") is { } retrieval
                ? RunbookRetrievalReader.Read(retrieval)
                : null,
            Steps = StepsOf(spec),
        };

        Require(parsed);

        return WithCompletionShorthand(parsed);
    }

    /// <summary>
    /// Applies the rules that cross a spec.
    /// </summary>
    /// <remarks>
    /// Every one of these would otherwise fire mid-turn, in front of a user, with money already spent - which is why the
    /// document is refused when it is applied rather than when it is first used.
    /// </remarks>
    private static void Require(RunbookSpec spec)
    {
        if (spec.Steps.Count == 0)
        {
            throw new FormatException("a runbook needs at least one step");
        }

        if (spec.Shape is null && spec.Collections.Count == 0)
        {
            throw new FormatException("a runbook needs spec.shape (v1) or spec.collections (v2)");
        }

        if (spec.Shape is not null && spec.Collections.Count > 0)
        {
            throw new FormatException("spec.shape and spec.collections are mutually exclusive");
        }

        // A semantic view is asked through the intent model task. A document that binds one and pins no such task would
        // refuse every turn at the layer with `intent-unresolved`, so it is refused here instead.
        var hasIntentTask = spec.Models?.Tasks.ContainsKey(TaskLevels.Intent) == true;
        var unmodelled = spec.DataViews.FirstOrDefault(view => view.Kind.IsSemantic() && !hasIntentTask);

        if (unmodelled is { } view)
        {
            throw new FormatException(
                $"spec.dataViews.{view.Name}: a semantic data view (kind {view.Kind}) needs `models.tasks.intent` - "
                    + "the model task that turns the question into measures and dimensions");
        }

        // A document with no profiles, no views and no default has nothing for the profile rules to say, which is the
        // case the original also skips. Everything else is checked here, at load.
        var profiles = spec.Retrieval?.ResearchProfiles ?? [];

        if (profiles.Count == 0 && spec.DataViews.Count == 0 && spec.Retrieval?.DefaultResearchProfile is null)
        {
            return;
        }

        // The profile half is enforced here, at load, exactly as the original enforces it: a profile that a turn would
        // have to refuse is a document that was never correct.
        if (ResearchValidation.Validate(
            profiles,
            spec.DataViews,
            spec.Retrieval?.DefaultResearchProfile,
            [.. spec.Collections.Select(collection => collection.Name)],
            spec.Completion?.ContextCharBudget) is { } problem)
        {
            throw new FormatException(problem.Detail);
        }
    }

    /// <summary>
    /// Folds the completion's provider and model shorthands into the model tasks, so resolution has one place to look.
    /// </summary>
    private static RunbookSpec WithCompletionShorthand(RunbookSpec spec)
    {
        if (spec.Completion is not { Provider: null, Model: null })
        {
            return spec;
        }

        if (spec.Completion is null)
        {
            return spec;
        }

        var models = spec.Models ?? new ModelsSpec();
        var tasks = new SortedDictionary<string, ModelSpec>(models.Tasks.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal), StringComparer.Ordinal);

        var completion = tasks.TryGetValue(TaskLevels.Completion, out var pinned) ? pinned : new ModelSpec();

        tasks[TaskLevels.Completion] = completion with
        {
            Provider = completion.Provider ?? spec.Completion.Provider,
            Model = completion.Model ?? spec.Completion.Model,
        };

        return spec with { Models = models with { Tasks = tasks } };
    }

    private static string? Text(YamlMappingNode mapping, string name, string path) =>
        RunbookYaml.OptionalText(mapping, name, path);

    private static CollectionSpec CollectionOf(YamlMappingNode mapping, string path)
    {
        return new CollectionSpec
        {
            Name = Text(mapping, "name", $"{path}.name")
                ?? throw new FormatException($"{path}.name is required"),
            Shape = Text(mapping, "shape", $"{path}.shape")
                ?? throw new FormatException($"{path}.shape is required"),
            AccessLevel = (int)(RunbookYaml.Integer(mapping, "accessLevel", $"{path}.accessLevel") ?? 0),
            Compartments = RunbookYaml.Strings(mapping, "compartments", $"{path}.compartments"),
            Sources = RunbookYaml.Map(mapping, "sources", $"{path}.sources") is { } binding
                ? new SourceBinding
                {
                    FilenamePrefix = Text(binding, "filenamePrefix", $"{path}.sources.filenamePrefix"),
                    MediaTypes = RunbookYaml.Strings(binding, "mediaTypes", $"{path}.sources.mediaTypes"),
                    ContentHashes = RunbookYaml.Strings(binding, "contentHashes", $"{path}.sources.contentHashes"),
                }
                : null,
            Evidence = RunbookYaml.Map(mapping, "evidence", $"{path}.evidence") is { } evidence
                ? new CollectionEvidence
                {
                    Labels = RunbookYaml.Strings(evidence, "labels", $"{path}.evidence.labels"),
                }
                : null,
        };
    }

    private static SourcesSpec? SourceLocationOf(YamlMappingNode spec) =>
        RunbookYaml.Map(spec, "sources", "spec.sources") is { } sources
            ? new SourcesSpec
            {
                Container = Text(sources, "container", "spec.sources.container"),
                Prefix = Text(sources, "prefix", "spec.sources.prefix"),
            }
            : null;

    private static ExecutionOrder ExecutionOrderOf(YamlMappingNode execution)
    {
        var order = Text(execution, "order", "spec.execution.order");

        return order switch
        {
            null or "stepMajor" => ExecutionOrder.StepMajor,
            "collectionMajor" => ExecutionOrder.CollectionMajor,
            _ => throw new FormatException(
                $"spec.execution.order must be 'stepMajor' or 'collectionMajor', got '{order}'"),
        };
    }

    private static DataViewDeclaration DataViewOf(YamlMappingNode mapping, string path)
    {
        var kind = Text(mapping, "kind", $"{path}.kind");

        return new DataViewDeclaration
        {
            Name = Text(mapping, "name", $"{path}.name")
                ?? throw new FormatException($"{path}.name is required"),

            // Pinned, always: a contract that changed under a profile is a different question being answered.
            Contract = Text(mapping, "contract", $"{path}.contract")
                ?? throw new FormatException($"{path}.contract is required"),
            Kind = kind switch
            {
                null or "contract" => DataViewKind.Contract,
                "metric_view" => DataViewKind.MetricView,
                "data_view" => DataViewKind.DataView,
                _ => throw new FormatException(
                    $"{path}.kind must be 'contract', 'metric_view' or 'data_view', got '{kind}'"),
            },
            Description = Text(mapping, "description", $"{path}.description"),
            Parameters = RunbookYaml.Map(mapping, "parameters", $"{path}.parameters") is { } parameters
                ? parameters.Children.ToDictionary(
                    pair => RunbookYaml.Text(pair.Key, $"{path}.parameters"),
                    pair => ParameterOf(
                        RunbookYaml.AsMapping(pair.Value, $"{path}.parameters.{RunbookYaml.Text(pair.Key, path)}"),
                        $"{path}.parameters.{RunbookYaml.Text(pair.Key, path)}"),
                    StringComparer.Ordinal)
                : new SortedDictionary<string, DataViewParameter>(StringComparer.Ordinal),
            AccessLevel = (int)(RunbookYaml.Integer(mapping, "accessLevel", $"{path}.accessLevel") ?? 0),
            Compartments = RunbookYaml.Strings(mapping, "compartments", $"{path}.compartments"),
        };
    }

    private static DataViewParameter ParameterOf(YamlMappingNode mapping, string path) => new()
    {
        Type = Text(mapping, "type", $"{path}.type")
            ?? throw new FormatException($"{path}.type is required"),

        // Kept as text whatever the declared type is: a decimal parameter that round-tripped through a number arrives
        // at the source having lost the precision the contract was written to keep.
        Value = Text(mapping, "value", $"{path}.value")
            ?? throw new FormatException($"{path}.value is required"),
    };

    private static ModelsSpec? ModelsOf(YamlMappingNode spec)
    {
        if (RunbookYaml.Map(spec, "models", "spec.models") is not { } models)
        {
            return null;
        }

        var tasks = new SortedDictionary<string, ModelSpec>(StringComparer.Ordinal);

        if (RunbookYaml.Map(models, "tasks", "spec.models.tasks") is { } declared)
        {
            foreach (var (name, value) in declared.Children)
            {
                var key = RunbookYaml.Text(name, "spec.models.tasks");

                tasks[key] = ModelOf(
                    RunbookYaml.AsMapping(value, $"spec.models.tasks.{key}"),
                    $"spec.models.tasks.{key}");
            }
        }

        return new ModelsSpec
        {
            Default = RunbookYaml.Map(models, "default", "spec.models.default") is { } fallback
                ? ModelOf(fallback, "spec.models.default")
                : null,
            Tasks = tasks,
            AllowOverrides = OverridesOf(
                RunbookYaml.Member(models, "allowOverrides"),
                "spec.models.allowOverrides"),
        };
    }

    private static ModelSpec ModelOf(YamlMappingNode mapping, string path) => new()
    {
        Provider = Text(mapping, "provider", $"{path}.provider"),
        Model = Text(mapping, "model", $"{path}.model"),
        Tier = Text(mapping, "tier", $"{path}.tier"),
    };

    /// <summary>
    /// Reads the override policy, which the document spells three ways.
    /// </summary>
    /// <remarks>
    /// Absent, a flag, or a list of provider names. Absent means nothing may be overridden, which is the closed default:
    /// a document that says nothing about overrides does not thereby permit them.
    /// </remarks>
    private static OverridePolicy OverridesOf(YamlNode? node, string path) => node switch
    {
        null => OverridePolicy.None,
        YamlSequenceNode sequence => new OverridePolicy
        {
            Allowlist = [.. sequence.Children.Select(item => RunbookYaml.Text(item, path))],
        },
        YamlScalarNode scalar => (scalar.Value ?? string.Empty).ToLowerInvariant() switch
        {
            "true" or "yes" or "on" => OverridePolicy.Everything,
            "false" or "no" or "off" => OverridePolicy.None,
            _ => throw new FormatException($"{path} must be true, false or a list of provider names"),
        },
        _ => throw new FormatException($"{path} must be true, false or a list of provider names"),
    };

    private static CompletionSpec CompletionOf(YamlMappingNode completion)
    {
        const string Path = "spec.completion";

        return new CompletionSpec
        {
            Provider = Text(completion, "provider", $"{Path}.provider"),
            Model = Text(completion, "model", $"{Path}.model"),

            // The template is carried as written. That it has to interpolate the served context is a validation finding
            // rather than a parse error: the document is readable either way, and an operator may be mid-edit.
            PromptTemplate = Text(completion, "promptTemplate", $"{Path}.promptTemplate")
                ?? throw new FormatException($"{Path}.promptTemplate is required"),
            Verification = RunbookYaml.Map(completion, "verification", $"{Path}.verification") is { } verification
                ? new VerificationSpec
                {
                    Quotes = RunbookYaml.Boolean(verification, "quotes", $"{Path}.verification.quotes") ?? false,
                    Citations = RunbookYaml.Boolean(verification, "citations", $"{Path}.verification.citations") ?? false,
                    MaxRetries = (int)(RunbookYaml.Integer(
                        verification, "maxRetries", $"{Path}.verification.maxRetries") ?? 1),
                }
                : null,
            ContextCharBudget = RunbookYaml.Integer(completion, "contextCharBudget", $"{Path}.contextCharBudget") is { } budget
                ? (int)budget
                : null,
            MaxTokens = RunbookYaml.Integer(completion, "maxTokens", $"{Path}.maxTokens") is { } tokens
                ? (int)tokens
                : null,
        };
    }

    /// <summary>
    /// Reads the steps.
    /// </summary>
    /// <remarks>
    /// Each step is a single-key map, which is how the document spells an enum with a body: <c>- buildIndex: {}</c>. A
    /// map with two keys is not a step, and guessing which one was meant would run something nobody asked for.
    /// </remarks>
    private static List<RunbookStep> StepsOf(YamlMappingNode spec)
    {
        var steps = new List<RunbookStep>();
        var raw = RunbookYaml.Items(spec, "steps", "spec.steps");

        for (var index = 0; index < raw.Count; index++)
        {
            var path = $"spec.steps[{index}]";
            var map = RunbookYaml.AsMapping(raw[index], path);

            if (map.Children.Count != 1)
            {
                throw new FormatException("each step must be a single-key map like `- buildIndex: {}`");
            }

            var key = RunbookYaml.Text(map.Children.Keys.First(), path);
            var fields = map.Children.Values.First() as YamlMappingNode;

            steps.Add(key switch
            {
                "resolveSources" => new RunbookStep { Kind = StepKind.ResolveSources },
                "buildIndex" => new RunbookStep { Kind = StepKind.BuildIndex },
                "verify" => new RunbookStep { Kind = StepKind.Verify },
                "verifyDataViews" => new RunbookStep { Kind = StepKind.VerifyDataViews },
                "cutover" => new RunbookStep { Kind = StepKind.Cutover, Approval = ApprovalOf(fields, path) },
                "retireOld" => new RunbookStep { Kind = StepKind.RetireOld, KeepVersions = KeepOf(fields, path) },
                _ => throw new FormatException($"unknown step '{key}'"),
            });
        }

        return steps;
    }

    /// <summary>
    /// Reads a cutover's approval, which is a closed vocabulary.
    /// </summary>
    /// <remarks>
    /// Anything else used to parse and mean "no approval": <c>approval: Required</c>, <c>approval: true</c> and
    /// <c>approval: require</c> all silently removed the human gate. A typo in the one field that pauses a cutover must
    /// not fail open.
    /// </remarks>
    private static string? ApprovalOf(YamlMappingNode? fields, string path)
    {
        if (fields is null || RunbookYaml.Member(fields, "approval") is not { } approval)
        {
            return null;
        }

        var value = RunbookYaml.Text(approval, $"{path}.approval");

        return value switch
        {
            "required" => "required",
            "none" => null,
            _ => throw new FormatException($"cutover.approval must be 'required' or 'none', got '{value}'"),
        };
    }

    /// <summary>
    /// Reads how many versions a retire keeps.
    /// </summary>
    /// <remarks>
    /// Range-checked rather than truncated: the original read this as a wider integer and a value above the ceiling
    /// became 0, which means "reclaim everything".
    /// </remarks>
    private static int KeepOf(YamlMappingNode? fields, string path) =>
        fields is null
            ? DefaultKeep
            : Checked(RunbookYaml.Integer(fields, "keep_versions", $"{path}.keep_versions"));

    private static int Checked(long? keep) => keep switch
    {
        null => DefaultKeep,
        >= 0 and <= uint.MaxValue => (int)keep.Value,
        _ => throw new FormatException(
            $"retireOld.keep_versions must be an integer between 0 and {uint.MaxValue}"),
    };

    /// <summary>The versions a retire keeps when it names none.</summary>
    private const int DefaultKeep = 2;
}
