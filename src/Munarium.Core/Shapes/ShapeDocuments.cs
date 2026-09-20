namespace Munarium.Shapes;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The shape document form: one shape as JSON, which is what a deployment reads at startup and writes when an operator
/// publishes one.
/// </summary>
/// <remarks>
/// The form lives here rather than where the files are read, because both ends need it: the loader that reads a
/// directory and the authoring path that publishes what a draft materialized. One format read two ways would be two
/// formats the day one of them changed.
/// </remarks>
public static class ShapeDocuments
{
    /// <summary>Reads one shape from its document form.</summary>
    /// <param name="json">The shape as JSON.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="JsonException">Thrown when the document is not a shape.</exception>
    public static FactShape Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var document = JsonSerializer.Deserialize(json, ShapeJson.Default.ShapeDocument)
            ?? throw new JsonException("The shape document is empty.");

        // The schema is carried as JSON text from here on: that is what the validator takes, and what a digest over a
        // shape's identity would have to agree with.
        return new FactShape
        {
            Name = document.Name,
            Version = document.Version,
            Identity = document.Identity,
            Schema = document.Schema.GetRawText(),
        };
    }

    /// <summary>Writes one shape in its document form.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>The document as JSON.</returns>
    public static string Write(FactShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        using var schema = JsonDocument.Parse(shape.Schema);

        return JsonSerializer.Serialize(
            new ShapeDocument
            {
                Name = shape.Name,
                Version = shape.Version,
                Identity = shape.Identity,
                Schema = schema.RootElement.Clone(),
            },
            ShapeJson.Default.ShapeDocument);
    }
}

/// <summary>The on-disk shape document.</summary>
public sealed record ShapeDocument
{
    /// <summary>Gets the shape name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the shape version.</summary>
    public required int Version { get; init; }

    /// <summary>Gets the body keys that identify a claim lineage.</summary>
    public required IReadOnlyList<string> Identity { get; init; }

    /// <summary>Gets the JSON Schema a fact body must satisfy.</summary>
    public required JsonElement Schema { get; init; }
}

/// <summary>Serialization metadata for shape documents.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ShapeDocument))]
public sealed partial class ShapeJson : JsonSerializerContext;