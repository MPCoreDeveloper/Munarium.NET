namespace Munarium.Wire;

using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>
/// The contract this build implements, as the deployment serves it.
/// </summary>
/// <remarks>
/// The specification is embedded in the assembly that owns the contract, so the document a caller generates from is
/// the document this build was generated from rather than a copy that could be a release behind.
/// <para>
/// It travels as JSON, because that is what the contract says the answer is. The conversion is a walk over the YAML
/// representation - one node at a time, reading each scalar as its own quoting says it reads - so the document's
/// shape is preserved rather than modelled, and no type here has to be kept in step with the specification it
/// describes.
/// </para>
/// </remarks>
public static class ContractDocument
{
    /// <summary>The embedded resource the contract ships in.</summary>
    public const string ResourceName = "Munarium.Wire.munarium.v1.yaml";

    private static readonly Lazy<string> Json = new(Map, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The contract, as a JSON document.</summary>
    /// <remarks>
    /// Mapped once and cached: the document cannot change while the process runs, and serving it is a read of what
    /// was already read.
    /// </remarks>
    /// <returns>The document's text.</returns>
    public static string AsJson() => Json.Value;

    private static string Map()
    {
        using var stream = typeof(ContractDocument).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"the contract is not embedded as '{ResourceName}'.");

        using var reader = new StreamReader(stream, Encoding.UTF8);

        var yaml = new YamlStream();
        yaml.Load(reader);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            Write(writer, yaml.Documents[0].RootNode);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();

                foreach (var (name, value) in mapping.Children)
                {
                    // A JSON object's names are strings whatever the YAML wrote them as, which is what keeps a
                    // quoted status code quoted and an unquoted one just as readable.
                    writer.WritePropertyName(Name(name));
                    Write(writer, value);
                }

                writer.WriteEndObject();
                break;

            case YamlSequenceNode sequence:
                writer.WriteStartArray();

                foreach (var item in sequence.Children)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            case YamlScalarNode scalar:
                WriteScalar(writer, scalar);
                break;

            // A document is mappings, sequences and scalars in the core schema. Anchors resolve to the node they
            // name, so anything else is a kind of YAML this contract does not use and a mapping here would be a
            // guess: refused loudly rather than served as something it is not.
            default:
                throw new InvalidOperationException(
                    $"the contract contains a YAML node of kind '{node.NodeType}', which is not a document node.");
        }
    }

    /// <summary>Writes one scalar as the kind of value its own quoting says it is.</summary>
    /// <remarks>
    /// A quoted scalar is text - that is what quoting means - and an unquoted one is what the YAML core schema reads
    /// it as: a boolean, a null, an integer, a number, or text. Writing every scalar as text would hand a consumer
    /// <c>"additionalProperties": "true"</c> for a contract that says <c>true</c>, which is a document that no
    /// longer describes this deployment.
    /// </remarks>
    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;

        // Quoting is the document saying "this is text": a quoted "true" is a string in YAML and has to stay one in
        // JSON, which is why the style decides before the words do.
        if (scalar.Style is not ScalarStyle.Plain and not ScalarStyle.Any)
        {
            writer.WriteStringValue(text);
            return;
        }

        switch (text)
        {
            case "true" or "True" or "TRUE":
                writer.WriteBooleanValue(true);
                return;
            case "false" or "False" or "FALSE":
                writer.WriteBooleanValue(false);
                return;
            case "null" or "Null" or "NULL" or "~" or "":
                writer.WriteNullValue();
                return;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            writer.WriteNumberValue(integer);
            return;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            writer.WriteNumberValue(number);
            return;
        }

        writer.WriteStringValue(text);
    }

    private static string Name(YamlNode node) =>
        node is YamlScalarNode scalar && scalar.Value is { Length: > 0 } name
            ? name
            : throw new InvalidOperationException(
                $"the contract contains a key of kind '{node.NodeType}', which cannot name a field.");
}
