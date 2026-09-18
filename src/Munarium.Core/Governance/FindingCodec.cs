namespace Munarium.Governance;

using System.Text.Json;
using System.Text.Json.Nodes;
using Munarium.Claims;
using Munarium.Ledger;

/// <summary>
/// The canonical encoding of a write's findings, for the ledger payload.
/// </summary>
/// <remarks>
/// A finding is the record of a verdict, so it has to survive the trip to storage as exactly the verdict
/// that was returned - which is why this is a fixed-order writer rather than a serializer: no reflection,
/// no options object that can drift between runtime versions, and nothing that would reorder a field and
/// quietly change what an operator reads back.
/// <para>
/// The severity travels as its numeric value, the same way a claim type does: the member names are free to
/// change, the encoding is not. The detail is written as the JSON it already is, so the structured part of
/// a finding keeps its shape instead of being flattened into text.
/// </para>
/// </remarks>
public static class FindingCodec
{
    /// <summary>The event type recorded when a write produced findings.</summary>
    public const string FindingsEventType = "gate.findings";

    private const int FirstSeverity = (int)Severity.Info;
    private const int LastSeverity = (int)Severity.Block;

    /// <summary>
    /// Determines whether an event type carries findings.
    /// </summary>
    /// <param name="eventType">The event type discriminator.</param>
    /// <returns><see langword="true"/> when the event carries findings.</returns>
    public static bool IsFindingsEvent(string eventType) => eventType is FindingsEventType;

    /// <summary>
    /// Encodes a write's findings into a canonical payload.
    /// </summary>
    /// <param name="findings">The findings, in the order they were produced.</param>
    /// <returns>The canonical UTF-8 payload.</returns>
    public static byte[] Encode(IReadOnlyList<GateFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return PayloadJson.Write(writer =>
        {
            writer.WriteStartArray();

            foreach (var finding in findings)
            {
                ArgumentNullException.ThrowIfNull(finding);

                writer.WriteStartObject();
                writer.WriteString("rule_id", finding.RuleId);
                writer.WriteNumber("severity", (int)finding.Severity);
                writer.WriteString("message", finding.Message);
                writer.WriteString("scope_path", finding.ScopePath);
                writer.WritePropertyName("detail");

                if (finding.Detail is { } detail)
                {
                    detail.WriteTo(writer);
                }
                else
                {
                    writer.WriteNullValue();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <summary>
    /// Decodes a write's findings from a canonical payload.
    /// </summary>
    /// <param name="payload">The canonical UTF-8 payload.</param>
    /// <returns>The findings, in the order they were encoded.</returns>
    /// <exception cref="FormatException">Thrown when the payload is not a canonical findings batch.</exception>
    public static IReadOnlyList<GateFinding> Decode(ReadOnlySpan<byte> payload)
    {
        var findings = new List<GateFinding>();

        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("A findings payload is an array of findings.");
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                findings.Add(Finding(element));
            }
        }
        catch (JsonException error)
        {
            throw new FormatException("A findings payload is not valid JSON.", error);
        }

        return findings;
    }

    private static GateFinding Finding(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("A finding is a JSON object.");
        }

        var ruleId = PayloadJson.Text(element, "rule_id") ?? throw new FormatException("A finding needs a rule id.");

        return new GateFinding
        {
            RuleId = ruleId,
            Severity = SeverityOf(element),
            Message = PayloadJson.Text(element, "message") ?? string.Empty,
            ScopePath = PayloadJson.Text(element, "scope_path"),
            Detail = Detail(element),
        };
    }

    // The severity is bounds-checked rather than reflected over, and an unknown value is refused rather
    // than mapped onto something plausible: a verdict filed under the wrong severity is worse than a
    // payload that fails to read.
    private static Severity SeverityOf(JsonElement element)
    {
        if (!element.TryGetProperty("severity", out var severity) || severity.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException("A finding needs a numeric severity.");
        }

        var number = severity.GetInt32();

        return number is >= FirstSeverity and <= LastSeverity
            ? (Severity)number
            : throw new FormatException($"Unknown severity '{number}' in a findings payload.");
    }

    private static JsonObject? Detail(JsonElement element) =>
        element.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(detail.GetRawText()) as JsonObject
            : null;
}
