namespace Munarium.Claims;

using System.Text.Json;
using Munarium.Ledger;

/// <summary>
/// The canonical encoding of an entity, for the ledger payload.
/// </summary>
/// <remarks>
/// An entity is what lets the ledger answer "are these two claims about the same thing" when the wording
/// differs, so its aliases travel with it rather than being resolved on the way in. A merge is a pointer
/// (<c>merged_into</c>) rather than a rewrite of history, which is what keeps a claim that named the
/// absorbed entity resolvable and the merge itself readable at a pin.
/// </remarks>
public static class EntityCodec
{
    /// <summary>The event type recorded when an entity is resolved.</summary>
    public const string ResolvedEventType = "entity.resolved";

    /// <summary>Determines whether an event type carries an entity.</summary>
    /// <param name="eventType">The event type discriminator.</param>
    /// <returns><see langword="true"/> when the event carries an entity.</returns>
    public static bool IsEntityEvent(string eventType) => eventType is ResolvedEventType;

    /// <summary>
    /// Encodes an entity into its canonical payload.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The canonical UTF-8 payload.</returns>
    public static byte[] Encode(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return PayloadJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("entity_id", entity.Id);
            writer.WriteString("version_id", entity.VersionId);
            writer.WriteString("canonical_name", entity.CanonicalName);
            PayloadJson.Optional(writer, "entity_type", entity.EntityType);
            PayloadJson.Optional(writer, "merged_into", entity.MergedInto);

            // The order aliases were seen in is not part of what an entity is, so they are written sorted: a
            // payload then depends on the set and not on the path that built it.
            writer.WriteStartArray("aliases");

            foreach (var alias in entity.Aliases.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(alias);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Decodes an entity, stamping it with the position its event landed at.
    /// </summary>
    /// <param name="payload">The canonical payload.</param>
    /// <param name="position">The position the event landed at.</param>
    /// <returns>The entity.</returns>
    /// <exception cref="FormatException">Thrown when the payload is not a canonical entity.</exception>
    public static Entity Decode(ReadOnlySpan<byte> payload, SequenceNumber position)
    {
        using var document = PayloadJson.Read(payload, "entity");
        var root = document.RootElement;

        return new Entity
        {
            Id = PayloadJson.RequiredText(root, "entity_id", "An entity"),
            VersionId = PayloadJson.RequiredText(root, "version_id", "An entity"),
            CanonicalName = PayloadJson.RequiredText(root, "canonical_name", "An entity"),
            EntityType = PayloadJson.Text(root, "entity_type"),
            Aliases = Aliases(root),
            Sequence = position,
            MergedInto = PayloadJson.Text(root, "merged_into"),
        };
    }

    private static IReadOnlyList<string> Aliases(JsonElement root) =>
        root.TryGetProperty("aliases", out var aliases) && aliases.ValueKind is JsonValueKind.Array
            ? [.. aliases.EnumerateArray().Select(alias => alias.GetString() ?? string.Empty)]
            : [];
}
