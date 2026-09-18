namespace Munarium.Providers.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Munarium.Evidence;

/// <summary>
/// The semantic plane's parser, held against the examples that ship with the contract.
/// </summary>
/// <remarks>
/// Not against JSON written here, deliberately. Hand-written fixtures are how the original's first parser came to
/// expect <c>{columns, rows, completeness}</c> while the plane actually returned a tagged block wrapping a manifest:
/// the tests agreed with the code and both were wrong about the peer. An example that ships with the contract cannot
/// drift from it.
/// </remarks>
public class MatrixResultTests
{
    [Fact]
    public void TheContractsCountExampleParsesWithItsCoverage()
    {
        using var answer = Example("evidence-block.count.json");

        var block = MatrixResult.Parse(answer.RootElement, "v");
        var count = CountOf(block);

        // A string in the contract, and deliberately so: the same exact-decimal discipline that keeps 900000.50
        // distinct from 900000.5.
        Assert.Equal(1284, count.Value);
        Assert.Equal(1284, count.RowsCovered);
        Assert.Equal(17, count.RowsExcluded);
        Assert.Contains("row policy denied", count.ExclusionReason, StringComparison.Ordinal);
        Assert.NotNull(count.EvidenceId);

        // A count that excluded 17 rows still supports a completeness claim, because it says so: the exclusion is part
        // of the evidence and not a caveat the answer has to guess at.
        Assert.True(block.SupportsCompleteness());
    }

    [Fact]
    public void TheContractsTableExampleKeepsThePlanesOwnRowIds()
    {
        using var answer = Example("evidence-block.complete-table.json");

        var block = MatrixResult.Parse(answer.RootElement, "v");
        var table = TableOf(block);

        Assert.Equal(["region", "pipeline_amount", "opportunity_count"], table.Columns);

        // The sharpest thing in this parser. `identity.row_id_rule` is `keys`, so a row's id is its KEY - EMEA, not
        // r0003. Numbering them here would mean the model cites the id it was shown while the checker resolves an
        // invented one, and every correct citation is rejected.
        Assert.Equal(["AMER", "APAC", "EMEA"], table.RowIds);
        Assert.False(table.Truncated);

        // Exact decimals survive as text.
        Assert.Equal("1180250.50", table.Rows[1][1]);
        Assert.NotNull(table.EvidenceId);
        Assert.True(block.SupportsCompleteness());
    }

    [Fact]
    public void ATruncatedManifestBeatsTheBlocksOwnFlag()
    {
        using var answer = Example("evidence-block.complete-table.json");

        var body = Mutate(answer.RootElement, root =>
        {
            root["manifest"]!["completeness"]!["truncated"] = true;
            root["truncated"] = false;
        });

        // The manifest is the sealed statement; the block's `truncated` is a convenience copy. Believing the copy would
        // let a truncated read back a completeness claim.
        using var mutated = JsonDocument.Parse(body);

        Assert.False(MatrixResult.Parse(mutated.RootElement, "v").SupportsCompleteness());
    }

    [Fact]
    public void TheContractsRefusalExampleKeepsItsCodeAndDropsItsMessage()
    {
        using var answer = Example("evidence-block.refusal.json");

        var refusal = RefusalOf(MatrixResult.Parse(answer.RootElement, "revenue"));

        Assert.Equal("required_evidence_not_permitted", refusal.Code);
        Assert.Equal("revenue", refusal.Source);

        // The plane's message is written for an operator of that plane and may name a source, a class or a column this
        // caller has no clearance for. The code is what an operator acts on.
        Assert.DoesNotContain("research profile", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountWithNoReadableValueRefusesRatherThanReportingZero()
    {
        using var answer = Example("evidence-block.count.json");

        var body = Mutate(answer.RootElement, root => root["value"] = "not a number");

        using var mutated = JsonDocument.Parse(body);

        // 0 would be an answer. "I could not read it" is not the same answer, and a count is the shape a reader trusts
        // most.
        Assert.Equal(
            EvidenceRefusalCodes.SourceUnavailable,
            RefusalOf(MatrixResult.Parse(mutated.RootElement, "v")).Code);
    }

    [Fact]
    public void AnUnrecognisedBlockKindRefusesInsteadOfBeingGuessedAt()
    {
        using var answer = Example("evidence-block.count.json");

        var body = Mutate(answer.RootElement, root => root["kind"] = "something_new");

        using var mutated = JsonDocument.Parse(body);

        var refusal = RefusalOf(MatrixResult.Parse(mutated.RootElement, "v"));

        Assert.Equal(EvidenceRefusalCodes.SourceUnavailable, refusal.Code);
        Assert.Contains("something_new", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullAndTheEmptyStringStayDistinctCells()
    {
        using var answer = Example("evidence-block.complete-table.json");

        var body = Mutate(answer.RootElement, root => root["rows"] = Rows()
            .Row("a", "900000.50", null)
            .Row("b", "900000.5", string.Empty));

        using var mutated = JsonDocument.Parse(body);

        var table = TableOf(MatrixResult.Parse(mutated.RootElement, "v"));

        // NULL and the empty string are different facts, and the fixture plants both.
        Assert.NotEqual(table.Rows[0][0], table.Rows[1][0]);
        Assert.Null(table.Rows[0][1]);
        Assert.Equal(string.Empty, table.Rows[1][1]);
    }

    [Fact]
    public void RowsWithoutASealedIdFallBackToPosition()
    {
        using var answer = Example("evidence-block.complete-table.json");

        var body = Mutate(answer.RootElement, root => root["rows"] = Rows().Row(null, "x", "1.00", "1"));

        using var mutated = JsonDocument.Parse(body);

        var table = TableOf(MatrixResult.Parse(mutated.RootElement, "v"));

        // `position` is a legal row_id_rule, and one-based matches how the context renders them.
        Assert.Equal(["r0001"], table.RowIds);
    }

    private static CountBlock CountOf(EvidenceBlock block) =>
        block is CountBlock count
            ? count
            : throw new Xunit.Sdk.XunitException($"expected a count, got {block.KindName()}");

    private static TableBlock TableOf(EvidenceBlock block) =>
        block is TableBlock table
            ? table
            : throw new Xunit.Sdk.XunitException($"expected a table, got {block.KindName()}");

    private static EvidenceRefusal RefusalOf(EvidenceBlock block) =>
        block is EvidenceRefusal refusal
            ? refusal
            : throw new Xunit.Sdk.XunitException($"expected a refusal, got {block.KindName()}");

    private static JsonDocument Example(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractExamples(), name)));

    /// <summary>
    /// Rewrites an example, so a test can hold one property against the contract's own body.
    /// </summary>
    /// <remarks>
    /// Mutating a shipped example rather than writing a second fixture keeps the tests reading the same bytes the
    /// contract ships: what changes is the one property under test, and everything around it stays the peer's.
    /// </remarks>
    private static string Mutate(JsonElement root, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(root.GetRawText())!.AsObject();

        change(node);

        return node.ToJsonString();
    }

    /// <summary>Finds the contract's examples by walking up from the test binary to the repository root.</summary>
    private static string ContractExamples()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "contract", "matrix", "examples");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("contract/matrix/examples was not found above the test binary.");
    }

    private static JsonArray Rows() => [];
}

/// <summary>
/// Builds the rows array a mutated example carries.
/// </summary>
/// <remarks>
/// At the namespace level because an extension method has to be, and an extension is what lets a test read as the array
/// it is building: <c>Rows().Row("a", ...).Row("b", ...)</c>.
/// </remarks>
internal static class JsonRows
{
    /// <summary>Adds a row, with no id when the plane sent none.</summary>
    /// <param name="rows">The array.</param>
    /// <param name="rowId">The row's sealed id, or <see langword="null"/>.</param>
    /// <param name="cells">The cells.</param>
    /// <returns>The same array.</returns>
    internal static JsonArray Row(this JsonArray rows, string? rowId, params string?[] cells)
    {
        var row = new JsonObject();

        if (rowId is not null)
        {
            row["row_id"] = rowId;
        }

        var values = new JsonArray();

        foreach (var cell in cells)
        {
            values.Add(cell is null ? null : JsonValue.Create(cell));
        }

        row["cells"] = values;
        rows.Add(row);

        return rows;
    }
}
