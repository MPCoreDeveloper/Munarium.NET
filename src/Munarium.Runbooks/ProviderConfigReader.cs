namespace Munarium.Runbooks;

using Munarium.Providers;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Reads a provider configuration document: the declaration a deployment applies.
/// </summary>
/// <remarks>
/// The same shape the original reads, field for field: <c>apiVersion</c>, <c>kind</c>, <c>metadata.name</c> and a
/// <c>spec</c> naming a dialect, an endpoint, the models that dialect serves and where the credential lives. It is read
/// beside the runbook reader because that is where this port's YAML reader is - the one that maps nodes by hand and
/// refuses a field nobody reads - and what it reads into is the kernel's own vocabulary.
/// <para>
/// Two differences from the original are deliberate and said here rather than left to be discovered: a field this
/// reader does not know is refused rather than ignored, because a misspelled <c>endpont</c> would otherwise silently
/// use the dialect's default endpoint; and a declared rate budget is refused by name, because the relay that would
/// enforce one is not served yet and a ceiling nobody enforces reads like a promise.
/// </para>
/// </remarks>
public static class ProviderConfigReader
{
    /// <summary>
    /// Reads a provider configuration.
    /// </summary>
    /// <param name="yaml">The document.</param>
    /// <returns>The declaration, or the reason it cannot be read.</returns>
    public static ProviderConfigOutcome Read(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        try
        {
            return Parse(yaml);
        }
        catch (FormatException problem)
        {
            return new ProviderConfigRefused(problem.Message);
        }
        catch (YamlException problem)
        {
            // A document that is not YAML at all is reported with the parser's own complaint attached, because the
            // complaint is what points at the line.
            return new ProviderConfigRefused($"provider config yaml: {problem.Message}");
        }
    }

    private static ProviderConfigOutcome Parse(string yaml)
    {
        var stream = new YamlStream();

        stream.Load(new StringReader(yaml));

        if (stream.Documents.Count != 1)
        {
            throw new FormatException("provider config yaml: a provider config is exactly one document");
        }

        var root = RunbookYaml.AsMapping(stream.Documents[0].RootNode, "provider config");

        RunbookYaml.DenyUnknown(root, "provider config", "apiVersion", "kind", "metadata", "spec");

        if (RunbookYaml.OptionalText(root, "apiVersion", "apiVersion") is null or { Length: 0 })
        {
            throw new FormatException("apiVersion is required");
        }

        var kind = RunbookYaml.OptionalText(root, "kind", "kind") ?? string.Empty;

        if (!string.Equals(kind, "ProviderConfig", StringComparison.Ordinal))
        {
            throw new FormatException($"kind must be ProviderConfig, got '{kind}'");
        }

        var metadata = RunbookYaml.AsMapping(
            RunbookYaml.Member(root, "metadata") ?? throw new FormatException("metadata is required"),
            "metadata");

        RunbookYaml.DenyUnknown(metadata, "metadata", "name");

        var name = (RunbookYaml.OptionalText(metadata, "name", "metadata.name") ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            throw new FormatException("metadata.name must be non-empty");
        }

        var spec = Spec(root);
        var declaration = new ProviderDeclaration
        {
            Name = name,
            Provider = new ProviderId(Provider(spec)),
            Endpoint = RunbookYaml.OptionalText(spec, "endpoint", "spec.endpoint"),
            Models = Models(spec),
            Credential = Credential(spec),
            OpenRouterProvider = RunbookYaml.OptionalText(spec, "openrouterProvider", "spec.openrouterProvider"),
            Budgets = Budgets(spec),
        };

        // The declaration's own rules judge it, so a document that is well formed and still unusable is refused with
        // the same words whichever surface it arrived on.
        return ProviderDeclaration.Refusal(declaration) is { } refused
            ? new ProviderConfigRefused(refused)
            : declaration;
    }

    /// <summary>Reads the spec, refusing what this port does not carry.</summary>
    /// <param name="root">The document's root.</param>
    /// <returns>The spec mapping.</returns>
    private static YamlMappingNode Spec(YamlMappingNode root)
    {
        var spec = RunbookYaml.AsMapping(
            RunbookYaml.Member(root, "spec") ?? throw new FormatException("spec is required"),
            "spec");

        RunbookYaml.DenyUnknown(
            spec,
            "spec",
            "provider",
            "endpoint",
            "models",
            "credentialRef",
            "openrouterProvider",
            "budgets");

        return spec;
    }

    /// <summary>Reads the dialect, which the declaration's own rules then check.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>The dialect's name.</returns>
    private static string Provider(YamlMappingNode spec) =>
        RunbookYaml.OptionalText(spec, "provider", "spec.provider") ?? string.Empty;

    /// <summary>Reads the models a configuration serves.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>The models, empty when the configuration names none.</returns>
    private static ProviderModels Models(YamlMappingNode spec)
    {
        if (RunbookYaml.Member(spec, "models") is not { } node)
        {
            return new ProviderModels();
        }

        var models = RunbookYaml.AsMapping(node, "spec.models");

        RunbookYaml.DenyUnknown(models, "spec.models", "complete", "embed", "fast", "capable", "frontier");

        return new ProviderModels
        {
            Complete = RunbookYaml.Strings(models, "complete", "spec.models.complete"),
            Embed = RunbookYaml.Strings(models, "embed", "spec.models.embed"),
            Fast = RunbookYaml.OptionalText(models, "fast", "spec.models.fast"),
            Capable = RunbookYaml.OptionalText(models, "capable", "spec.models.capable"),
            Frontier = RunbookYaml.OptionalText(models, "frontier", "spec.models.frontier"),
        };
    }

    /// <summary>Reads the rate and token ceilings a configuration declares.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>The ceilings, empty when the configuration declares none.</returns>
    private static ProviderBudgets Budgets(YamlMappingNode spec)
    {
        if (RunbookYaml.Member(spec, "budgets") is not { } node)
        {
            return ProviderBudgets.None;
        }

        var budgets = RunbookYaml.AsMapping(node, "spec.budgets");

        RunbookYaml.DenyUnknown(budgets, "spec.budgets", "rpm", "tpm", "dailyTokens");

        return new ProviderBudgets
        {
            RequestsPerMinute = Whole(budgets, "rpm"),
            TokensPerMinute = Whole(budgets, "tpm"),
            Fast = DailyToken(budgets, "fast"),
            Capable = DailyToken(budgets, "capable"),
            Frontier = DailyToken(budgets, "frontier"),
        };
    }

    /// <summary>Reads a whole-number ceiling, bounded the way the original bounds one.</summary>
    /// <param name="budgets">The budgets mapping.</param>
    /// <param name="name">The member's name.</param>
    /// <returns>The value, or <see langword="null"/> when it is not declared.</returns>
    private static int? Whole(YamlMappingNode budgets, string name)
    {
        var declared = RunbookYaml.Integer(budgets, name, $"spec.budgets.{name}");

        return declared switch
        {
            null => null,
            > 0 and <= int.MaxValue => (int)declared.Value,
            _ => throw new FormatException($"spec.budgets.{name} must be a positive whole number"),
        };
    }

    /// <summary>Reads one daily token ceiling out of the tier mapping.</summary>
    /// <param name="budgets">The budgets mapping.</param>
    /// <param name="tier">The tier's name.</param>
    /// <returns>The ceiling, or <see langword="null"/> when the tier is unlimited.</returns>
    private static long? DailyToken(YamlMappingNode budgets, string tier)
    {
        if (RunbookYaml.Map(budgets, "dailyTokens", "spec.budgets.dailyTokens") is not { } daily)
        {
            return null;
        }

        RunbookYaml.DenyUnknown(daily, "spec.budgets.dailyTokens", "fast", "capable", "frontier");

        var declared = RunbookYaml.Integer(daily, tier, $"spec.budgets.dailyTokens.{tier}");

        return declared switch
        {
            null => null,
            > 0 => declared,
            _ => throw new FormatException($"spec.budgets.dailyTokens.{tier} must be a positive whole number"),
        };
    }

    /// <summary>Reads where the credential lives - never the credential.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>The reference, or <see langword="null"/> when the configuration names none.</returns>
    private static CredentialReference? Credential(YamlMappingNode spec)
    {
        if (RunbookYaml.Member(spec, "credentialRef") is not { } node)
        {
            return null;
        }

        var reference = RunbookYaml.AsMapping(node, "spec.credentialRef");

        RunbookYaml.DenyUnknown(reference, "spec.credentialRef", "env", "file");

        var environment = RunbookYaml.OptionalText(reference, "env", "spec.credentialRef.env");
        var file = RunbookYaml.OptionalText(reference, "file", "spec.credentialRef.file");

        return (environment, file) switch
        {
            (null or "", null or "") =>
                throw new FormatException("spec.credentialRef names neither an environment variable nor a file"),
            ({ Length: > 0 }, null or "") => CredentialReference.ForEnvironment(environment),
            (null or "", { Length: > 0 }) => CredentialReference.ForFile(file),
            _ => throw new FormatException(
                "spec.credentialRef names both an environment variable and a file; it is one or the other"),
        };
    }
}
