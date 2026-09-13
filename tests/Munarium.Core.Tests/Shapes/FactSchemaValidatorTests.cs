namespace Munarium.Core.Tests.Shapes;

using Munarium.Shapes;

/// <summary>
/// Tests for the schema subset a shape validates with, including that the errors it produces are
/// stable enough to be written into a ledger entry.
/// </summary>
public class FactSchemaValidatorTests
{
    private const string Schema = """
        {
          "type": "object",
          "required": ["vendor_id", "status"],
          "additionalProperties": false,
          "properties": {
            "vendor_id": { "type": "string", "minLength": 1, "pattern": "^v-[0-9]+$" },
            "status": { "type": "string", "enum": ["approved","pending"] },
            "score": { "type": "number", "minimum": 0, "maximum": 1 },
            "tags": { "type": "array", "items": { "type": "string" }, "maxItems": 2 }
          }
        }
        """;

    [Fact]
    public void AConformingBodyPasses()
    {
        var result = Validate("""{"vendor_id":"v-1","status":"approved"}""");

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AMissingRequiredPropertyIsReportedWithItsPath()
    {
        var result = Validate("""{"vendor_id":"v-1"}""");

        Assert.False(result.Valid);
        Assert.Equal(["$: required property 'status' is missing."], result.Errors);
    }

    [Fact]
    public void AnUnknownPropertyIsRefusedWhenTheSchemaSaysSo()
    {
        var result = Validate("""{"vendor_id":"v-1","status":"approved","extra":1}""");

        Assert.Equal(["$: property 'extra' is not allowed."], result.Errors);
    }

    [Fact]
    public void AWrongTypeIsReportedOnceRatherThanDescendedInto()
    {
        var result = Validate("""{"vendor_id":7,"status":"approved"}""");

        Assert.Equal(["$.vendor_id: expected string, found number."], result.Errors);
    }

    [Fact]
    public void AValueOutsideAnEnumIsRefused()
    {
        var result = Validate("""{"vendor_id":"v-1","status":"sanctioned"}""");

        Assert.Equal(["""$.status: value is not one of ["approved","pending"]."""], result.Errors);
    }

    [Fact]
    public void LengthAndPatternBothReportOnTheSameString()
    {
        var result = Validate("""{"vendor_id":"","status":"approved"}""");

        Assert.Equal(
            [
                "$.vendor_id: shorter than the minimum length 1.",
                "$.vendor_id: does not match the pattern ^v-[0-9]+$.",
            ],
            result.Errors);
    }

    [Fact]
    public void NumericBoundsAreEnforced()
    {
        Assert.Equal(
            ["$.score: 2 is above the maximum 1."],
            Validate("""{"vendor_id":"v-1","status":"approved","score":2}""").Errors);

        Assert.Equal(
            ["$.score: -1 is below the minimum 0."],
            Validate("""{"vendor_id":"v-1","status":"approved","score":-1}""").Errors);
    }

    [Fact]
    public void ArrayItemsAndBoundsAreEnforced()
    {
        Assert.Equal(
            ["$.tags: has 3 items, more than the maximum 2."],
            Validate("""{"vendor_id":"v-1","status":"approved","tags":["a","b","c"]}""").Errors);

        Assert.Equal(
            ["$.tags[1]: expected string, found number."],
            Validate("""{"vendor_id":"v-1","status":"approved","tags":["a",3]}""").Errors);
    }

    [Fact]
    public void ANullBodyIsValidatedRatherThanAssumedValid()
    {
        Assert.Equal(["$: expected object, found null."], Validate(null).Errors);
    }

    [Fact]
    public void KeywordsOutsideTheSubsetAreIgnoredRatherThanFailingTheBody()
    {
        var result = FactSchemaValidator.Validate(
            """{"type":"string","format":"email","description":"an address"}""",
            """ "someone@example.com" """);

        Assert.True(result.Valid);
    }

    [Fact]
    public void SeveralViolationsAreReportedInADeterministicOrder()
    {
        var first = Validate("""{"status":"sanctioned","extra":1}""").Errors;
        var second = Validate("""{"status":"sanctioned","extra":1}""").Errors;

        Assert.Equal(
            [
                "$: required property 'vendor_id' is missing.",
                """$.status: value is not one of ["approved","pending"].""",
                "$: property 'extra' is not allowed.",
            ],
            first);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ABodyThatIsNotJsonIsAVerdictRatherThanAnException()
    {
        var result = Validate("not json at all");

        Assert.False(result.Valid);
        Assert.Equal(["$: the body is not valid JSON (line 0, position 1)."], result.Errors);
    }

    private static SchemaValidation Validate(string? body) => FactSchemaValidator.Validate(Schema, body);
}
