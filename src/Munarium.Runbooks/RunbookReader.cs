namespace Munarium.Runbooks;

using Munarium.Evidence;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Reads a runbook from the YAML an operator applies.
/// </summary>
/// <remarks>
/// Parsing rejects structurally broken documents, and the rules here are the ones that would otherwise fire mid-turn, in
/// front of a user, with money already spent: a document with no steps runs nothing, one naming both a shape and
/// collections does not say which path it takes, a semantic data view with no <c>intent</c> task can only ever refuse at
/// the layer with <c>intent-unresolved</c>, and a profile that is not coherent fails closed when it is applied.
/// <para>
/// The profile half is checked through <see cref="ResearchValidation"/> rather than re-implemented, so a document
/// applied through this reader and one applied through the kernel's own path cannot disagree.
/// </para>
/// </remarks>
public static class RunbookReader
{
    /// <summary>
    /// Reads a runbook.
    /// </summary>
    /// <param name="yaml">The document.</param>
    /// <returns>The document, or the reason it cannot be read.</returns>
    public static (RunbookDocument? Document, string? Problem) Read(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        try
        {
            return (Parse(yaml), null);
        }
        catch (FormatException problem)
        {
            return (null, problem.Message);
        }
        catch (YamlException problem)
        {
            // A document that is not YAML at all is reported with the parser's own complaint attached, because the
            // complaint is what points at the line.
            return (null, $"runbook yaml: {problem.Message}");
        }
    }

    private static RunbookDocument Parse(string yaml)
    {
        var stream = new YamlStream();

        stream.Load(new StringReader(yaml));

        if (stream.Documents.Count != 1)
        {
            throw new FormatException("runbook yaml: a runbook is exactly one document");
        }

        var root = RunbookYaml.AsMapping(stream.Documents[0].RootNode, "runbook");

        // The kind exists so a document handed to the wrong reader is refused rather than half-understood: a shape or a
        // provider configuration has the same YAML shape and no meaning here.
        var kind = RunbookYaml.OptionalText(root, "kind", "kind") ?? string.Empty;

        if (!string.Equals(kind, "Runbook", StringComparison.Ordinal))
        {
            throw new FormatException($"kind must be Runbook, got '{kind}'");
        }

        var metadata = RunbookYaml.AsMapping(
            RunbookYaml.Member(root, "metadata") ?? throw new FormatException("metadata is required"),
            "metadata");

        // '@' separates name from version in a runbook ref, and the version is compared numerically: a name carrying one
        // would poison both. Refused here rather than discovered by whoever resolves the ref.
        var name = RunbookYaml.OptionalText(metadata, "name", "metadata.name") ?? string.Empty;

        if (name.Trim().Length == 0 || name.Contains('@', StringComparison.Ordinal))
        {
            throw new FormatException($"runbook name '{name}' must be non-empty and must not contain '@'");
        }

        var version = RunbookYaml.Integer(metadata, "version", "metadata.version") ?? 0;

        if (version is < 0 or > uint.MaxValue)
        {
            throw new FormatException("metadata.version must be an integer between 0 and 4294967295");
        }

        var spec = RunbookYaml.AsMapping(
            RunbookYaml.Member(root, "spec") ?? throw new FormatException("spec is required"),
            "spec");

        return new RunbookDocument
        {
            ApiVersion = RunbookYaml.OptionalText(root, "apiVersion", "apiVersion"),
            Kind = kind,
            Metadata = new RunbookMeta { Name = name, Version = (int)version },
            Spec = RunbookSpecs.Read(spec),
        };
    }
}
