using System.Text;
using System.Text.Json;

namespace Munarium.Shapes;

/// <summary>
/// A versioned, declarative description of what a claim looks like.
/// </summary>
/// <remarks>
/// A shape is what lets a workload - contract clauses, patent office actions, regulatory letters -
/// be absorbed without code changes to the kernel. It carries the two things the write path needs:
/// the schema a fact body must satisfy, and the identity that says which facts supersede each other.
/// </remarks>
public sealed record FactShape
{
    /// <summary>Gets the shape name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the shape version. Two versions are two shapes.</summary>
    public required int Version { get; init; }

    /// <summary>Gets the body keys that identify a claim lineage.</summary>
    public required IReadOnlyList<string> Identity { get; init; }

    /// <summary>Gets the JSON Schema the fact body must satisfy.</summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Derives the lineage key from a fact body: the identity fields, in order, canonically joined.
    /// </summary>
    /// <param name="bodyJson">The fact body; <see langword="null"/> is treated as JSON null.</param>
    /// <returns>
    /// The lineage. A key the body does not carry contributes an empty value rather than failing, so
    /// even a malformed body gets a stable lineage - which is what lets a blocked claim still be
    /// recorded as disputed instead of being unrecordable.
    /// </returns>
    public string LineageOf(string? bodyJson)
    {
        using var body = JsonDocument.Parse(bodyJson ?? "null");
        var lineage = new StringBuilder(Name).Append('@').Append(Version);

        foreach (var key in Identity)
        {
            lineage.Append('|').Append(key).Append('=');

            if (body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty(key, out var value) &&
                value.ValueKind != JsonValueKind.Null)
            {
                lineage.Append(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
            }
        }

        return lineage.ToString();
    }
}
