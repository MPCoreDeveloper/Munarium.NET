using System.Text.Json;
using System.Text.RegularExpressions;

namespace Munarium.Shapes;

/// <summary>
/// The result of validating a fact body against a shape's schema.
/// </summary>
/// <param name="Valid">Whether the body satisfied the schema.</param>
/// <param name="Errors">Why it did not, in a deterministic order.</param>
public sealed record SchemaValidation(bool Valid, IReadOnlyList<string> Errors)
{
    /// <summary>Creates a passing result.</summary>
    /// <returns>A valid result.</returns>
    public static SchemaValidation Passing() => new(true, []);

    /// <summary>Creates a failing result.</summary>
    /// <param name="errors">The reasons.</param>
    /// <returns>An invalid result.</returns>
    public static SchemaValidation Failing(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>
/// A deterministic validator for the JSON Schema subset a Munarium shape uses.
/// </summary>
/// <remarks>
/// It is deliberately a named subset rather than a full JSON Schema implementation, and deliberately
/// in the kernel rather than behind a library: a shape decides whether a claim is asserted or
/// recorded as disputed, so the answer has to be reproducible, the errors have to be stable enough
/// to write into a ledger entry, and the whole thing has to survive NativeAOT.
/// <para>
/// Supported keywords: <c>type</c>, <c>const</c>, <c>enum</c>, <c>required</c>, <c>properties</c>,
/// <c>additionalProperties: false</c>, <c>items</c>, <c>minItems</c>, <c>maxItems</c>,
/// <c>minLength</c>, <c>maxLength</c>, <c>pattern</c>, <c>minimum</c>, <c>maximum</c>. Anything else
/// is ignored, so a richer schema validates as its supported subset rather than failing open.
/// </para>
/// </remarks>
public static class FactSchemaValidator
{
    private const string Root = "$";

    // A hostile pattern must not be able to hang the write path.
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Validates a fact body against a schema.
    /// </summary>
    /// <param name="schemaJson">The shape's schema.</param>
    /// <param name="bodyJson">The fact body; <see langword="null"/> is treated as JSON null.</param>
    /// <returns>The validation result.</returns>
    public static SchemaValidation Validate(string schemaJson, string? bodyJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);

        // A schema that does not parse is a deployment bug, so it throws. A body that does not parse is
        // a claim, and a claim can only ever come back as a verdict - never as an exception on the
        // write path.
        using var schema = JsonDocument.Parse(schemaJson);

        JsonDocument body;

        try
        {
            body = JsonDocument.Parse(bodyJson ?? "null");
        }
        catch (JsonException malformed)
        {
            return SchemaValidation.Failing(
                [
                    $"{Root}: the body is not valid JSON "
                        + $"(line {malformed.LineNumber}, position {malformed.BytePositionInLine}).",
                ]);
        }

        using (body)
        {
            var errors = new List<string>();
            ValidateNode(schema.RootElement, body.RootElement, Root, errors);

            return errors.Count == 0 ? SchemaValidation.Passing() : SchemaValidation.Failing(errors);
        }
    }

    private static void ValidateNode(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var expected = type.GetString()!;
            if (!MatchesType(expected, value))
            {
                errors.Add($"{path}: expected {expected}, found {Describe(value)}.");
                return;
            }
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, value))
        {
            errors.Add($"{path}: expected the constant {constant.GetRawText()}.");
        }

        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array &&
            !allowed.EnumerateArray().Any(candidate => JsonEquals(candidate, value)))
        {
            errors.Add($"{path}: value is not one of {allowed.GetRawText()}.");
        }

        ValidateNumbers(schema, value, path, errors);
        ValidateStrings(schema, value, path, errors);

        if (value.ValueKind == JsonValueKind.Array)
        {
            ValidateArray(schema, value, path, errors);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(schema, value, path, errors);
        }
    }

    private static void ValidateNumbers(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        if (TryGetNumber(schema, "minimum", out var minimum) && value.GetDouble() < minimum)
        {
            errors.Add($"{path}: {value.GetDouble()} is below the minimum {minimum}.");
        }

        if (TryGetNumber(schema, "maximum", out var maximum) && value.GetDouble() > maximum)
        {
            errors.Add($"{path}: {value.GetDouble()} is above the maximum {maximum}.");
        }
    }

    private static void ValidateStrings(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = value.GetString()!;

        if (TryGetInt(schema, "minLength", out var minLength) && text.Length < minLength)
        {
            errors.Add($"{path}: shorter than the minimum length {minLength}.");
        }

        if (TryGetInt(schema, "maxLength", out var maxLength) && text.Length > maxLength)
        {
            errors.Add($"{path}: longer than the maximum length {maxLength}.");
        }

        if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String &&
            !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, PatternTimeout))
        {
            errors.Add($"{path}: does not match the pattern {pattern.GetString()}.");
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var length = value.GetArrayLength();

        if (TryGetInt(schema, "minItems", out var minItems) && length < minItems)
        {
            errors.Add($"{path}: has {length} items, fewer than the minimum {minItems}.");
        }

        if (TryGetInt(schema, "maxItems", out var maxItems) && length > maxItems)
        {
            errors.Add($"{path}: has {length} items, more than the maximum {maxItems}.");
        }

        if (!schema.TryGetProperty("items", out var items))
        {
            return;
        }

        var index = 0;

        foreach (var element in value.EnumerateArray())
        {
            ValidateNode(items, element, $"{path}[{index}]", errors);
            index++;
        }
    }

    private static void ValidateObject(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String && !value.TryGetProperty(name.GetString()!, out _))
                {
                    errors.Add($"{path}: required property '{name.GetString()}' is missing.");
                }
            }
        }

        schema.TryGetProperty("properties", out var properties);
        var allowExtras = !(schema.TryGetProperty("additionalProperties", out var additional) &&
                            additional.ValueKind == JsonValueKind.False);

        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateNode(propertySchema, property.Value, $"{path}.{property.Name}", errors);
                continue;
            }

            if (!allowExtras)
            {
                errors.Add($"{path}: property '{property.Name}' is not allowed.");
            }
        }
    }

    private static bool MatchesType(string expected, JsonElement value) => expected switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };

    private static bool JsonEquals(JsonElement left, JsonElement right) =>
        left.ValueKind == right.ValueKind && left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => NumberEquals(left, right),
            _ => left.GetRawText() == right.GetRawText(),
        };

    // JSON Schema compares numbers by value, and exact comparison of the parsed value is what is
    // meant here - so this deliberately avoids floating-point equality, which would report 1 and
    // 1.0 as different.
    private static bool NumberEquals(JsonElement left, JsonElement right) =>
        left.TryGetInt64(out var leftInteger) && right.TryGetInt64(out var rightInteger)
            ? leftInteger == rightInteger
            : DecimalEquals(left, right);

    private static bool DecimalEquals(JsonElement left, JsonElement right) =>
        left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal)
            ? leftDecimal == rightDecimal
            : string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

    private static bool TryGetNumber(JsonElement schema, string name, out double value)
    {
        value = 0;
        return schema.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value);
    }

    private static bool TryGetInt(JsonElement schema, string name, out int value)
    {
        value = 0;
        return schema.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }
}
