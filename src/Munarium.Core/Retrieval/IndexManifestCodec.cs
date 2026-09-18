namespace Munarium.Retrieval;

using System.Text;
using System.Text.Json;
using Munarium.Ledger;

/// <summary>
/// How an index version's manifest is written down and read back.
/// </summary>
/// <remarks>
/// Hand-written rather than reflected, for the reason the ledger's payloads are: the shape is part of the record, so it
/// is stated in one place instead of being derived from whatever the type happens to look like today - and the read
/// ignores what it does not know, so adding a field to a manifest does not make yesterday's row unreadable. The same
/// JSON travels in a table column and in a payload, so the two can never disagree about what a manifest is.
/// </remarks>
public static class IndexManifestCodec
{
    /// <summary>Writes a manifest as JSON text.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(IndexManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var payload = PayloadJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("collection_id", manifest.CollectionId);
            writer.WriteString("collection_name", manifest.CollectionName);
            writer.WriteString("shape_ref", manifest.ShapeRef);
            writer.WriteString("engine", manifest.Engine);
            writer.WriteString("chunker", manifest.Chunker);
            writer.WriteString("extractors", manifest.Extractors);
            writer.WriteNumber("max_chars", manifest.MaxChars);

            // The embedder is nested rather than flattened because its parts only mean anything together: a model name
            // without its provider and width does not identify a vector space.
            writer.WriteStartObject("embedder");
            writer.WriteString("provider", manifest.Embedder.Provider);
            writer.WriteString("model", manifest.Embedder.Model);
            writer.WriteNumber("dimensions", manifest.Embedder.Dimensions);
            writer.WriteEndObject();

            writer.WriteStartArray("source_hashes");

            foreach (var hash in manifest.SourceContentHashes)
            {
                writer.WriteStringValue(hash);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>Reads a manifest back from JSON text.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="what">What is being read, for the failure message.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="FormatException">Thrown when the text is not JSON, or when a field the manifest needs is missing.</exception>
    public static IndexManifest FromJson(string json, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = PayloadJson.Read(Encoding.UTF8.GetBytes(json), what);
        var root = document.RootElement;
        var embedder = root.GetProperty("embedder");

        return new IndexManifest
        {
            CollectionId = PayloadJson.RequiredText(root, "collection_id", what),
            CollectionName = PayloadJson.RequiredText(root, "collection_name", what),
            ShapeRef = PayloadJson.RequiredText(root, "shape_ref", what),
            Engine = PayloadJson.RequiredText(root, "engine", what),
            Chunker = PayloadJson.RequiredText(root, "chunker", what),
            Extractors = PayloadJson.RequiredText(root, "extractors", what),
            MaxChars = (int)PayloadJson.RequiredNumber(root, "max_chars", what),
            Embedder = new EmbedderRef(
                PayloadJson.RequiredText(embedder, "provider", what),
                PayloadJson.RequiredText(embedder, "model", what),
                (int)PayloadJson.RequiredNumber(embedder, "dimensions", what)),
            SourceContentHashes = Hashes(root),
        };
    }

    private static List<string> Hashes(JsonElement root)
    {
        if (!root.TryGetProperty("source_hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var read = new List<string>(hashes.GetArrayLength());

        foreach (var hash in hashes.EnumerateArray())
        {
            read.Add(hash.GetString() ?? string.Empty);
        }

        return read;
    }
}
