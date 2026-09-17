using System.Globalization;
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
        AppendField(builder, "versionId", fact.VersionId);
        AppendField(builder, "claimType", ((int)fact.ClaimType).ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "lineage", fact.Lineage);
        AppendOptional(builder, "subject", fact.Subject);
        AppendOptional(builder, "key", fact.Key);
        AppendOptional(builder, "value", fact.Value);
        AppendOptional(builder, "scopePath", fact.ScopePath);
        AppendOptional(builder, "provenance", ((int)fact.Provenance).ToString(CultureInfo.InvariantCulture));
        AppendOptional(builder, "supersedesId", fact.SupersedesId);
        AppendOptional(builder, "entityId", fact.EntityId);
        AppendOptional(builder, "evidence", fact.EvidenceJson);
        AppendOptional(
            builder,
            "confidence",
            fact.Confidence?.ToString("R", CultureInfo.InvariantCulture));
        AppendOptional(builder, "shapeRef", fact.ShapeRef);
        AppendField(builder, "body", fact.Body);
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
            VersionId = Required(fields, "versionId"),
            ClaimType = ClaimTypeOf(Required(fields, "claimType")),
            Lineage = Required(fields, "lineage"),
            Subject = Optional(fields, "subject"),
            Key = Optional(fields, "key"),
            Value = Optional(fields, "value"),
            ScopePath = Nullable(fields, "scopePath"),
            Provenance = ProvenanceOf(Optional(fields, "provenance")),
            SupersedesId = Nullable(fields, "supersedesId"),
            EntityId = Nullable(fields, "entityId"),
            EvidenceJson = Nullable(fields, "evidence"),
            Confidence = ConfidenceOf(Optional(fields, "confidence")),
            ShapeRef = Nullable(fields, "shapeRef"),
            Body = Required(fields, "body"),
            Statement = Required(fields, "statement"),
            Actor = Required(fields, "actor"),
            Gate = Required(fields, "gate"),
            Reason = Required(fields, "reason"),
        };
    }

    // A null field would encode as the text "null" and silently change a digest, so a fact that
    // carries one is refused where it is produced, with the name of the field.
    private static void AppendField(StringBuilder builder, string name, string? value)
    {
        if (value is null)
        {
            throw new ArgumentException($"A fact field cannot be null: '{name}'.", nameof(value));
        }

        builder.Append(name).Append(FieldSeparator).Append(Escape(value)).Append(LineSeparator);
    }

    // The optional fields are written as empty when absent, and read back as absent. That is what keeps
    // the format additive: a payload written before a field existed decodes with that field absent rather
    // than failing, so an older ledger stays readable - which is the only reason a canonical form may
    // grow at all.
    private static void AppendOptional(StringBuilder builder, string name, string? value) =>
        builder.Append(name).Append(FieldSeparator).Append(Escape(value ?? string.Empty)).Append(LineSeparator);

    private static string Required(Dictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value)
            ? value
            : throw new FormatException($"Fact payload is missing '{name}'.");

    private static string Optional(Dictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) ? value : string.Empty;

    private static string? Nullable(Dictionary<string, string> fields, string name) =>
        Optional(fields, name) is { Length: > 0 } value ? value : null;

    // The claim type travels as its numeric value, and is bounds-checked rather than reflected over: the
    // member names are free to change, the encoding is not.
    private static ClaimType ClaimTypeOf(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        number is >= (int)ClaimType.Unspecified and <= (int)ClaimType.Correction
            ? (ClaimType)number
            : throw new FormatException($"Unknown claim type '{value}' in a fact payload.");

    // Provenance travels the same way, for the same reason. An absent field is the default rather than a
    // guess: every payload written before provenance existed was witnessed.
    private static Claims.Provenance ProvenanceOf(string value) =>
        value.Length == 0 ? Claims.Provenance.Witnessed : ParseProvenance(value);

    private static Claims.Provenance ParseProvenance(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        number is >= (int)Claims.Provenance.Witnessed and <= (int)Claims.Provenance.CoverageRepair
            ? (Claims.Provenance)number
            : throw new FormatException($"Unknown provenance '{value}' in a fact payload.");

    // Round-trippable and culture-free, so the same double encodes to the same bytes everywhere.
    private static double? ConfidenceOf(string value) =>
        value.Length == 0 ? null : ParseConfidence(value);

    private static double ParseConfidence(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"Unparseable confidence '{value}' in a fact payload.");

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
