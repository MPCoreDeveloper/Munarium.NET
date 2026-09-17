namespace Munarium.Ledger;

using System.Text.Json;

/// <summary>
/// The shared reader and writer for the JSON payloads the kernel writes itself.
/// </summary>
/// <remarks>
/// A fixed-order writer rather than a serializer: no reflection, no options object that can drift between
/// runtime versions, and nothing that reorders a field and quietly changes what a later reader sees. It is
/// shared so that a third payload does not become a third set of quoting rules.
/// <para>
/// The facts keep their own line format (<c>FactCodec</c>) because those bytes are hashed into a slice
/// digest and were chosen to be auditable by eye; a record with optional fields and numbers reads better as
/// JSON, so that is what findings and the snapshot planes use.
/// </para>
/// </remarks>
public static class PayloadJson
{
    /// <summary>
    /// Writes a payload.
    /// </summary>
    /// <param name="write">The writer's body; it receives the writer to fill.</param>
    /// <returns>The UTF-8 payload.</returns>
    public static byte[] Write(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Writes a string property, or JSON null when the value is absent.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value, or <see langword="null"/>.</param>
    public static void Optional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, value);
    }

    /// <summary>
    /// Reads a string property, or <see langword="null"/> when it is absent or not a string.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads a string property that cannot be absent.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <param name="what">What is being read, for the error.</param>
    /// <returns>The value.</returns>
    /// <exception cref="FormatException">Thrown when the property is absent or not a string.</exception>
    public static string RequiredText(JsonElement element, string name, string what) =>
        Text(element, name) ?? throw new FormatException($"{what} needs a '{name}'.");

    /// <summary>
    /// Reads an integer property that cannot be absent.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <param name="what">What is being read, for the error.</param>
    /// <returns>The value.</returns>
    /// <exception cref="FormatException">Thrown when the property is absent or not a number.</exception>
    public static long RequiredNumber(JsonElement element, string name, string what) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : throw new FormatException($"{what} needs a numeric '{name}'.");

    /// <summary>
    /// Reads a boolean property, or <see langword="false"/> when it is absent.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value.</returns>
    public static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Parses a payload into its root object.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="what">What is being read, for the error.</param>
    /// <returns>The parsed document, which the caller owns and disposes.</returns>
    /// <exception cref="FormatException">Thrown when the payload is not a JSON object.</exception>
    public static JsonDocument Read(ReadOnlySpan<byte> payload, string what)
    {
        try
        {
            var document = JsonDocument.Parse(payload.ToArray());

            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                document.Dispose();
                throw new FormatException($"A {what} payload is a JSON object.");
            }

            return document;
        }
        catch (JsonException error)
        {
            throw new FormatException($"A {what} payload is not valid JSON.", error);
        }
    }
}
