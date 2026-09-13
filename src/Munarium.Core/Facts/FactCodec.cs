using System.Text;

namespace Munarium.Facts;

/// <summary>
/// The canonical encoding of a <see cref="FactRecord"/> for the ledger payload.
/// </summary>
/// <remarks>
/// This format is part of the contract rather than an implementation detail: a slice digest is
/// computed over these bytes, so field order and escaping are fixed. It is deliberately not a
/// serializer - no reflection, no writer options, nothing that can drift between runtime versions
/// and quietly change a digest.
/// </remarks>
public static class FactCodec
{
    /// <summary>The event type recorded when governance permitted the fact.</summary>
    public const string AssertedEventType = "fact.asserted";

    /// <summary>The event type recorded when governance blocked the fact.</summary>
    public const string DisputedEventType = "fact.disputed";

    private const char FieldSeparator = '=';
    private const char LineSeparator = '\n';

    /// <summary>
    /// Determines whether an event type carries a fact.
    /// </summary>
    /// <param name="eventType">The event type discriminator.</param>
    /// <returns><see langword="true"/> when the event carries a fact.</returns>
    public static bool IsFactEvent(string eventType) =>
        eventType is AssertedEventType or DisputedEventType;

    /// <summary>
    /// Encodes a fact into its canonical payload bytes.
    /// </summary>
    /// <param name="fact">The fact to encode.</param>
    /// <returns>The canonical UTF-8 payload.</returns>
    public static byte[] Encode(FactRecord fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var builder = new StringBuilder();
        AppendField(builder, "claimId", fact.ClaimId);
        AppendField(builder, "lineage", fact.Lineage);
        AppendField(builder, "statement", fact.Statement);
        AppendField(builder, "actor", fact.Actor);
        AppendField(builder, "gate", fact.Gate);
        AppendField(builder, "reason", fact.Reason);

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Decodes a fact from its canonical payload bytes.
    /// </summary>
    /// <param name="payload">The canonical UTF-8 payload.</param>
    /// <returns>The decoded fact.</returns>
    /// <exception cref="FormatException">Thrown when the payload is not a canonical fact.</exception>
    public static FactRecord Decode(ReadOnlySpan<byte> payload)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in Encoding.UTF8.GetString(payload).Split(LineSeparator))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(FieldSeparator, StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new FormatException($"Malformed fact payload line: '{line}'.");
            }

            fields[line[..separator]] = Unescape(line[(separator + 1)..]);
        }

        return new FactRecord
        {
            ClaimId = Required(fields, "claimId"),
            Lineage = Required(fields, "lineage"),
            Statement = Required(fields, "statement"),
            Actor = Required(fields, "actor"),
            Gate = Required(fields, "gate"),
            Reason = Required(fields, "reason"),
        };
    }

    // A null field would encode as the text "null" and silently change a digest, so a fact that
    // carries one is refused where it is produced, with the name of the field.
    private static void AppendField(StringBuilder builder, string name, string value)
    {
        if (value is null)
        {
            throw new ArgumentException($"A fact field cannot be null: '{name}'.", nameof(value));
        }

        builder.Append(name).Append(FieldSeparator).Append(Escape(value)).Append(LineSeparator);
    }

    private static string Required(Dictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value)
            ? value
            : throw new FormatException($"Fact payload is missing '{name}'.");

    // Exactly three escapes exist, and only a backslash, a newline and a carriage return are ever
    // escaped - which is what keeps the encoding stable enough to hash.
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal);

    // A single left-to-right pass, so a literal backslash can never be mistaken for the start of an
    // escape introduced by an earlier replacement.
    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var index = 0;

        while (index < value.Length)
        {
            var current = value[index];
            index++;

            if (current != '\\')
            {
                builder.Append(current);
                continue;
            }

            if (index >= value.Length)
            {
                throw new FormatException("A fact payload ends with a dangling escape.");
            }

            var escape = value[index];
            index++;

            builder.Append(escape switch
            {
                'n' => '\n',
                'r' => '\r',
                '\\' => '\\',
                _ => throw new FormatException($"Unknown escape '\\{escape}' in a fact payload."),
            });
        }

        return builder.ToString();
    }
}
