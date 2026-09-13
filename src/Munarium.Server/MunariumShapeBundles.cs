namespace Munarium.Server;

using System.Text.Json;
using System.Text.Json.Serialization;
using Munarium.Shapes;

/// <summary>
/// Loads shapes from a directory of JSON files: one shape per file.
/// </summary>
/// <remarks>
/// Shapes are declarative and versioned, so they belong in files a deployment can read and a
/// reviewer can diff, not in code. A missing or unconfigured directory yields an empty registry,
/// which is a valid deployment: it can still read the ledger, it just cannot write a claim.
/// </remarks>
public static class MunariumShapeBundles
{
    /// <summary>
    /// Loads every shape under a directory.
    /// </summary>
    /// <param name="directory">The directory to read, or <see langword="null"/> for none.</param>
    /// <returns>The registry.</returns>
    public static ShapeRegistry Load(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new ShapeRegistry([]);
        }

        var shapes = new List<FactShape>();

        // Ordered, so which shape is registered first does not depend on the file system.
        foreach (var file in Directory
            .EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            shapes.Add(Read(File.ReadAllText(file)));
        }

        return new ShapeRegistry(shapes);
    }

    /// <summary>Reads one shape from its on-disk form.</summary>
    /// <param name="json">The shape as JSON.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="JsonException">Thrown when the document is not a shape.</exception>
    public static FactShape Read(string json)
    {
        var document = JsonSerializer.Deserialize(json, ShapeJson.Default.ShapeDocument)
            ?? throw new JsonException("The shape document is empty.");

        // The schema is carried as JSON text from here on: that is what the validator takes, and what
        // a digest over a shape's identity would have to agree with.
        return new FactShape
        {
            Name = document.Name,
            Version = document.Version,
            Identity = document.Identity,
            Schema = document.Schema.GetRawText(),
        };
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
