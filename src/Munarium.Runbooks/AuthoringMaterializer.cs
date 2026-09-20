namespace Munarium.Runbooks;

using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

/// <summary>What a draft materialized into: the documents, and what still has to be answered.</summary>
/// <param name="Documents">The path of each document, mapped to its text.</param>
/// <param name="Todos">The questions an author still has to answer, in the order they were noticed.</param>
public sealed record Materialized(IReadOnlyDictionary<string, string> Documents, IReadOnlyList<string> Todos)
{
    /// <summary>Gets the order the documents have to be applied in.</summary>
    /// <remarks>
    /// Shapes before everything else, each group in path order. A collection binds a shape, so a runbook applied before
    /// the shape it binds would materialize nothing for as long as that shape was missing - and a deployment that applied
    /// a whole set in the wrong order would have been serving half of it in between.
    /// </remarks>
    public IReadOnlyList<string> ApplyOrder =>
    [
        .. Documents.Keys
            .Where(path => path.StartsWith(AuthoringMaterializer.ShapeDirectory, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal),
        .. Documents.Keys
            .Where(path => !path.StartsWith(AuthoringMaterializer.ShapeDirectory, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal),
    ];
}

/// <summary>Deterministic materialization: interview answers into the documents a draft needs.</summary>
/// <remarks>
/// Deterministic and model-free, like the interview it follows: the same answers produce the same bytes, and an
/// unanswered required question produces a placeholder plus an entry in <see cref="Materialized.Todos"/> rather than a
/// failure, so a fresh draft is red TODOs and not a red document.
/// <para>
/// Everything emitted is proven by re-reading it through <see cref="RunbookReader"/>, which is the reader a deployment
/// applies it with - the same proof the original makes by re-parsing before a document leaves the module.
/// </para>
/// </remarks>
public static class AuthoringMaterializer
{
    /// <summary>The container documents are stored in.</summary>
    /// <summary>The directory a materialized shape lands under, which is what makes the apply order decidable.</summary>
    public const string ShapeDirectory = "shapes/";

    private const string Container = "sources";

    /// <summary>Builds a draft's runbook from interview answers.</summary>
    /// <param name="name">The draft name, which is the runbook name.</param>
    /// <param name="pattern">The chosen pattern, or <see langword="null"/> while none is chosen.</param>
    /// <param name="answers">One flat map of answers, keyed by question id.</param>
    /// <returns>The documents, or why nothing could be built.</returns>
    public static (Materialized? Set, string? Problem) Build(
        string name,
        AuthoringPattern? pattern,
        IReadOnlyDictionary<string, object?> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        name = (name ?? string.Empty).Trim();

        if (name.Length == 0 || name.Contains('@', StringComparison.Ordinal))
        {
            return (null, $"draft name '{name}' must be non-empty and must not contain '@'");
        }

        var todos = new List<string>();
        var shapeRef = $"{name}-documents@1";

        if (Text(answers, "identity.description") is null)
        {
            todos.Add("identity.description: describe the corpus and the question it answers");
        }

        // Normalized rather than submitted for validation: the rule is unambiguous and a prefix without its slash binds
        // the wrong documents, so reproducing the mistake for a validator to catch would be the wrong kind of help.
        var root = Text(answers, "prefix.root") is { } submitted
            ? Normalize(submitted)
            : Placeholder(
                todos,
                $"prefix.root: choose the path prefix, which is immutable once uploaded; defaulting to '{name}/'",
                $"{name}/");

        var areas = Areas(answers);
        var levels = Map(answers, "access.area_levels");
        var compartments = Map(answers, "access.area_compartments");
        var mediaTypes = Map(answers, "extraction.media_types");

        // Uniform defaults to true only when no per-area answer exists: an author who supplied levels and then had them
        // flattened to level 0 would have been told nothing.
        var uniform = Boolean(answers, "access.uniform_public") ?? (levels is null && compartments is null);
        var topK = Integer(answers, "retrieval.top_k") ?? 10;
        var rrfK = Integer(answers, "retrieval.rrf_k") ?? 60;
        var candidateN = Integer(answers, "retrieval.candidate_n") ?? 100;
        var cutoverApproval = Boolean(answers, "lifecycle.cutover_approval") ?? true;
        var keepVersions = Integer(answers, "lifecycle.keep_versions") ?? 2;
        if (Integer(answers, "retrieval.max_chars") is { } chunkChars)
        {
            todos.Add("retrieval.max_chars: this port cuts at a kernel constant (" + chunkChars.ToString(CultureInfo.InvariantCulture) + "), so the answer is recorded but not applied");
        }

        var completionApplies = pattern?.HasCompletion != false;
        var completionEnabled = completionApplies && (Boolean(answers, "completion.enabled") ?? true);
        var completionTier = Text(answers, "completion.tier") ?? "capable";
        var verifyQuotes = Boolean(answers, "completion.verification_quotes") ?? true;
        var verifyCitations = Boolean(answers, "completion.verification_citations") ?? true;

        var collections = new YamlSequenceNode();

        if (areas.Count == 0)
        {
            todos.Add(
                "prefix.areas: list the corpus folders, one per governance boundary - defaulting to a single "
                + "whole-prefix collection");

            collections.Add(Collection($"{name}-index", shapeRef, 0, [], root, []));
        }
        else
        {
            foreach (var path in areas)
            {
                var key = path.Trim('/');
                var level = uniform ? 0 : (int)IntegerOf(Lookup(levels, key));

                if (!uniform && Lookup(levels, key) is null)
                {
                    todos.Add($"access.area_levels: no level for area '{key}'; defaulting to 0");
                }

                collections.Add(Collection(
                    $"{name}-{Slug(key)}",
                    shapeRef,
                    level,
                    uniform ? [] : LookupList(compartments, key),
                    $"{root}{path}",
                    LookupList(mediaTypes, key)));
            }
        }

        var tasks = new YamlMappingNode
        {
            { "validation", new YamlMappingNode { { "provider", "local" }, { "tier", "fast" } } },
        };

        if (completionEnabled)
        {
            tasks.Add("completion", new YamlMappingNode { { "provider", "local" }, { "tier", completionTier } });
        }

        if (string.Equals(Text(answers, "retrieval.embedding"), "byok", StringComparison.Ordinal))
        {
            todos.Add("retrieval.embedding: a BYOK embedder is a measured choice this materializer does not write yet");
        }

        if (string.Equals(Text(answers, "completion.allow_overrides"), "all", StringComparison.Ordinal))
        {
            // Overrides here name the providers a caller may choose, so there is no way to say any: reporting that is
            // better than writing a form that means something else.
            todos.Add("completion.allow_overrides: permitting any is not expressible here; none was named");
        }

        var steps = new YamlSequenceNode
        {
            new YamlMappingNode { { "resolveSources", new YamlMappingNode() } },
            new YamlMappingNode { { "buildIndex", new YamlMappingNode() } },
            new YamlMappingNode { { "verify", new YamlMappingNode() } },
            new YamlMappingNode
            {
                {
                    "cutover",
                    cutoverApproval
                        ? new YamlMappingNode { { "approval", "required" } }
                        : NoStep
                },
            },
            new YamlMappingNode
            {
                { "retireOld", new YamlMappingNode { { "keep_versions", Scalar(keepVersions) } } },
            },
        };

        var spec = new YamlMappingNode
        {
            { "sources", new YamlMappingNode { { "container", Container }, { "prefix", root } } },
            { "collections", collections },
            {
                "retrieval",
                new YamlMappingNode { { "topK", Scalar(topK) }, { "rrfK", Scalar(rrfK) }, { "candidateN", Scalar(candidateN) } }
            },
            {
                "models",
                new YamlMappingNode
                {
                    { "default", new YamlMappingNode { { "provider", "local" }, { "tier", "capable" } } },
                    { "tasks", tasks },
                    { "allowOverrides", new YamlSequenceNode() },
                }
            },
            { "steps", steps },
        };

        if (completionEnabled)
        {
            spec.Add(
                "completion",
                new YamlMappingNode
                {
                    { "promptTemplate", PromptTemplate },
                    {
                        "verification",
                        new YamlMappingNode
                        {
                            { "quotes", Scalar(verifyQuotes) },
                            { "citations", Scalar(verifyCitations) },
                            { "maxRetries", Scalar(1) },
                        }
                    },
                });
        }

        var yaml = Emit(new YamlDocument(
            new YamlMappingNode
            {
                { "apiVersion", "munarium.ioka.io/v1" },
                { "kind", "Runbook" },
                { "metadata", new YamlMappingNode { { "name", name }, { "version", Scalar(1) } } },
                { "spec", spec },
            }));

        // The proof the original makes, made the same way: a document is only a document if the reader a deployment
        // applies it with can read it.
        if (RunbookReader.Read(yaml) is (null, { } refused))
        {
            return (null, $"the materialized runbook does not read: {refused}");
        }

        return (
            new Materialized(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [$"runbooks/{name}.yaml"] = yaml,
                    [$"shapes/{name}-documents.json"] = Shape($"{name}-documents", answers, todos),
                },
                todos),
            null);
    }

    /// <summary>An empty step body, for a step that carries nothing.</summary>
    private static readonly YamlMappingNode NoStep = [];
    /// <summary>The completion template a materialized runbook carries.</summary>
    /// <remarks>
    /// The measured lessons, in the template itself: cite what was read, say when the corpus does not establish an answer,
    /// and enumerate a whole set rather than sampling it, because a partial answer reads as complete.
    /// </remarks>
    private const string PromptTemplate =
        "Answer only from the evidence below and cite every claim as doc#node. If the evidence does not establish an "
        + "answer, say so plainly: a search hit you did not read is not a citation, and a partial answer to a whole-set "
        + "question reads as complete.\n\nEvidence:\n{context}\n\nQuestion: {query}\n";

    /// <summary>Builds one collection binding a prefix to its shape and access.</summary>
    private static YamlMappingNode Collection(
        string name,
        string shape,
        int level,
        IReadOnlyList<string> compartments,
        string prefix,
        IReadOnlyList<string> mediaTypes)
    {
        var sources = new YamlMappingNode { { "filenamePrefix", prefix } };

        if (mediaTypes.Count > 0)
        {
            sources.Add("mediaTypes", Sequence(mediaTypes));
        }

        var collection = new YamlMappingNode
        {
            { "name", name },
            { "shape", shape },
            { "accessLevel", Scalar(level) },
        };

        if (compartments.Count > 0)
        {
            collection.Add("compartments", Sequence(compartments));
        }

        collection.Add("sources", sources);

        return collection;
    }

    /// <summary>Writes a flag, as the two words YAML reads rather than the two a serializer would.</summary>
    /// <param name="value">The flag.</param>
    /// <returns>The node.</returns>
    private static YamlScalarNode Scalar(bool value) =>
        new(value ? "true" : "false");
    /// <summary>Writes a number, which is text in YAML like everything else.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The node.</returns>
    private static YamlScalarNode Scalar(long value) =>
        new(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static YamlSequenceNode Sequence(IEnumerable<string> values)
    {
        var sequence = new YamlSequenceNode();

        foreach (var value in values)
        {
            sequence.Add(new YamlScalarNode(value));
        }

        return sequence;
    }

    private static string Emit(YamlDocument document)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);

        new YamlStream(document).Save(writer, assignAnchors: false);

        return writer.ToString();
    }

    /// <summary>Builds the shape document a draft reads its facts through.</summary>
    /// <remarks>
    /// JSON rather than YAML, because this deployment loads shapes from JSON: a materialized shape is one it can drop
    /// into its shapes directory and use. The lineage is subject and key, which is the vocabulary own rule - the value is
    /// what changes over time and must not fork a lineage - and the two spellings travel with it, because a key carrying a
    /// dot silently steals from the subject when subject.key is split at the last dot.
    /// </remarks>
    /// <param name="shapeName">The name the draft reads its facts through.</param>
    /// <param name="answers">The answers.</param>
    /// <param name="todos">What still has to be answered, appended to in place.</param>
    /// <returns>The shape as JSON.</returns>
    private static string Shape(string shapeName, IReadOnlyDictionary<string, object?> answers, List<string> todos)
    {
        var required = new List<string> { "subject", "key", "value" };
        var declared = Fields(answers, todos);
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", shapeName);
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("identity");
            writer.WriteStringValue("subject");
            writer.WriteStringValue("key");
            writer.WriteEndArray();
            writer.WritePropertyName("schema");
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            StringField(writer, "subject", "^[a-z][a-z0-9_]{0,63}$", 0, 0);
            StringField(writer, "key", "^[a-z][a-z0-9_:-]{0,63}$", 0, 0);
            StringField(writer, "value", string.Empty, 1, 512);

            foreach (var (name, type, isRequired) in declared)
            {
                writer.WritePropertyName(name);
                writer.WriteStartObject();
                writer.WriteString("type", type);
                writer.WriteEndObject();

                if (isRequired)
                {
                    required.Add(name);
                }
            }

            writer.WriteEndObject();
            writer.WriteStartArray("required");

            foreach (var name in required)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes a string field with whatever constraints it carries.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="name">The field name.</param>
    /// <param name="pattern">The pattern, or empty for none.</param>
    /// <param name="minLength">The shortest value, or zero for none.</param>
    /// <param name="maxLength">The longest value, or zero for none.</param>
    private static void StringField(Utf8JsonWriter writer, string name, string pattern, int minLength, int maxLength)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("type", "string");

        if (pattern.Length > 0)
        {
            writer.WriteString("pattern", pattern);
        }

        if (minLength > 0)
        {
            writer.WriteNumber("minLength", minLength);
        }

        if (maxLength > 0)
        {
            writer.WriteNumber("maxLength", maxLength);
        }

        writer.WriteEndObject();
    }

    /// <summary>Reads the extra fact-body fields an author asked for, refusing what the vocabulary cannot carry.</summary>
    /// <remarks>
    /// Two refusals, both the original: a name that is not a lowercase field name is skipped, and a name from the core
    /// vocabulary is skipped, because a field named key would quietly replace the dot-free-key pattern the whole
    /// subject.key convention rests on. Both are reported rather than dropped in silence.
    /// </remarks>
    /// <param name="answers">The answers.</param>
    /// <param name="todos">What still has to be answered, appended to in place.</param>
    /// <returns>The fields that survived, with their type and whether they are required.</returns>
    private static List<(string Name, string Type, bool Required)> Fields(
        IReadOnlyDictionary<string, object?> answers,
        List<string> todos)
    {
        var fields = new List<(string Name, string Type, bool Required)>();

        if (!answers.TryGetValue("extraction.fact_fields", out var value) || value is not IReadOnlyList<object?> declared)
        {
            return fields;
        }

        foreach (var item in declared)
        {
            if (item is not IReadOnlyDictionary<string, object?> entry
                || !entry.TryGetValue("key", out var raw)
                || raw is not string name
                || name.Trim().Length == 0)
            {
                continue;
            }

            name = name.Trim();

            if (!IsFieldName(name))
            {
                todos.Add($"extraction.fact_fields: '{name}' is not a lowercase field name and was skipped");
                continue;
            }

            if (name is "subject" or "key" or "value")
            {
                todos.Add($"extraction.fact_fields: '{name}' is the core vocabulary and was skipped");
                continue;
            }

            fields.Add((
                name,
                entry.TryGetValue("type", out var declaredType) && declaredType is string kind ? kind : "string",
                entry.TryGetValue("required", out var wanted) && wanted is true));
        }

        return fields;
    }

    /// <summary>Whether a name is one the fact vocabulary carries.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether it is a lowercase letter followed by lowercase letters, digits or underscores.</returns>
    private static bool IsFieldName(string name) =>
        name.Length > 0
        && char.IsAsciiLetterLower(name[0])
        && name.All(character =>
            char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_');

    /// <summary>Reads a non-empty text answer.</summary>
    private static string? Text(IReadOnlyDictionary<string, object?> answers, string key) =>
        answers.TryGetValue(key, out var value) && value is string text && text.Trim().Length > 0
            ? text.Trim()
            : null;

    /// <summary>Reads a whole-number answer.</summary>
    private static long? Integer(IReadOnlyDictionary<string, object?> answers, string key) =>
        answers.TryGetValue(key, out var value)
            ? value switch
            {
                long number => number,
                int number => number,
                _ => null,
            }
            : null;

    /// <summary>Reads a yes-or-no answer.</summary>
    private static bool? Boolean(IReadOnlyDictionary<string, object?> answers, string key) =>
        answers.TryGetValue(key, out var value) && value is bool flag ? flag : null;

    /// <summary>Reads a per-area map answer.</summary>
    private static IReadOnlyDictionary<string, object?>? Map(
        IReadOnlyDictionary<string, object?> answers,
        string key) =>
        answers.TryGetValue(key, out var value) ? value as IReadOnlyDictionary<string, object?> : null;

    /// <summary>Reads one area's entry from a per-area map, with or without its trailing slash.</summary>
    private static object? Lookup(IReadOnlyDictionary<string, object?>? map, string key) =>
        map is null ? null : At(map, key);

    /// <summary>Reads a per-area entry under a key with or without the trailing slash its path carries.</summary>
    private static object? At(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value : Trailed(map, key);

    /// <summary>Reads the same entry under the spelling an area path carries in an answer.</summary>
    private static object? Trailed(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(string.Concat(key, "/"), out var value) ? value : null;

    /// <summary>Reads one area's entry as a whole number.</summary>
    private static long IntegerOf(object? value) => value switch
    {
        long number => number,
        int number => number,
        _ => 0L,
    };

    /// <summary>Reads one area's entry as a list of strings.</summary>
    private static IReadOnlyList<string> LookupList(IReadOnlyDictionary<string, object?>? map, string key) =>
        Lookup(map, key) is IReadOnlyList<object?> items ? [.. items.OfType<string>()] : [];

    /// <summary>Reads the corpus folders, dropping anything that would bind the whole root.</summary>
    /// <remarks>
    /// A path of one slash would put the entire corpus behind one collection whose name is malformed, so it is not an
    /// area - the original drops it for the same reason.
    /// </remarks>
    private static List<string> Areas(IReadOnlyDictionary<string, object?> answers)
    {
        var areas = new List<string>();

        if (!answers.TryGetValue("prefix.areas", out var value) || value is not IReadOnlyList<object?> declared)
        {
            return areas;
        }

        foreach (var item in declared)
        {
            if (item is not IReadOnlyDictionary<string, object?> entry
                || !entry.TryGetValue("path", out var raw)
                || raw is not string path)
            {
                continue;
            }

            var normalized = Normalize(path.Trim().TrimStart('/'));

            if (normalized.Length == 0)
            {
                continue;
            }

            areas.Add(normalized);
        }

        return areas;
    }

    /// <summary>Ends a prefix in a slash, because matching is a literal prefix.</summary>
    /// <param name="prefix">The prefix as answered.</param>
    /// <returns>The prefix as a binding needs it.</returns>
    private static string Normalize(string prefix) =>
        prefix.Length == 0 || prefix.EndsWith('/')
            ? prefix
            : string.Concat(prefix, "/");

    /// <summary>Folds a folder path into a collection name.</summary>
    /// <param name="value">The path.</param>
    /// <returns>The name fragment.</returns>
    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        }

        return string.Join(
            '-',
            builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Records an unanswered question and answers with the value that keeps the draft readable.</summary>
    private static T Placeholder<T>(List<string> todos, string todo, T value)
    {
        todos.Add(todo);

        return value;
    }
}
