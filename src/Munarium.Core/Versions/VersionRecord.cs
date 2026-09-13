namespace Munarium.Versions;

using System.Globalization;
using System.Text.Json;

/// <summary>
/// A memory version: the identity a claim is written under, and the node it occupies in a lineage.
/// </summary>
/// <remarks>
/// A version is not a table. It is an ordinary claim under the <c>version</c> shape, which is what makes it
/// governed, pinned and replayable like everything else in the ledger - and what lets the version graph be
/// rebuilt from a pin rather than materialised beside it.
/// </remarks>
public sealed record VersionRecord
{
    /// <summary>Gets the version's identity, which is also the stream its claims are written to.</summary>
    public required string VersionId { get; init; }

    /// <summary>Gets the version this one descends from, or an empty string when it starts a lineage.</summary>
    public string ParentVersionId { get; init; } = string.Empty;

    /// <summary>Gets the date this version is "as of", or <see langword="null"/> when it declares none.</summary>
    public DateOnly? AsOfDate { get; init; }

    /// <summary>Gets the label a human gave this version.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether this version starts a lineage.</summary>
    public bool IsRoot => string.IsNullOrEmpty(ParentVersionId);

    /// <summary>
    /// Reads a version from the JSON body its claim carried.
    /// </summary>
    /// <param name="bodyJson">The fact body.</param>
    /// <returns>The version, or <see langword="null"/> when the body does not describe one.</returns>
    public static VersionRecord? FromBody(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return null;
        }

        try
        {
            using var body = JsonDocument.Parse(bodyJson);

            if (body.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var versionId = Text(body.RootElement, "version_id");

            return string.IsNullOrWhiteSpace(versionId)
                ? null
                : new VersionRecord
                {
                    VersionId = versionId,
                    ParentVersionId = Text(body.RootElement, "parent_version_id") ?? string.Empty,
                    AsOfDate = Date(body.RootElement, "as_of"),
                    Label = Text(body.RootElement, "label") ?? string.Empty,
                };
        }
        catch (JsonException)
        {
            // A body that is not JSON is not a version. Whether it is a valid *claim* is the shape gate's
            // question, and that has been answered before anything reaches a slice.
            return null;
        }
    }

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateOnly? Date(JsonElement body, string name) =>
        Text(body, name) is { Length: > 0 } text &&
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
