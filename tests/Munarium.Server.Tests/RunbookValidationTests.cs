namespace Munarium.Server.Tests;

using Munarium.Shapes;
using Munarium.Wire;

/// <summary>Tests for validating a runbook document: deterministic, and never a refusal.</summary>
public class RunbookValidationTests
{
    /// <summary>A document that does not parse is a finding rather than an error, and a produced one has none.</summary>
    [Fact]
    public async Task ATruncatedDocumentIsAFindingRatherThanARefusal()
    {
        await using var kernel = MunariumKernel.Create(
            Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}"),
            "munarium-runbook-validate",
            new ShapeRegistry([]));

        // A document that does not read answers with one finding naming the parse, and a 200: an author editing a runbook
        // wants to read what is wrong with it, and a document that does not parse has one thing wrong with it.
        var truncated = string.Join((char)10, ["kind: Runbook", "metadata:", "  name: broken-without-a-spec"]);
        var refused = await kernel.Operations.ValidateRunbookAsync(new WireRunbookValidationRequest(truncated));

        Assert.False(refused.Valid);
        Assert.Equal(MunariumOperations.RunbookParseFindingCode, Assert.Single(refused.Findings).Code);
        Assert.Null(refused.SuggestNote);

        // The materializer output is what a good document looks like here, so validating it proves the checks pass on
        // something this deployment produced rather than on a fixture that happens to be well formed.
        var (set, _) = Munarium.Runbooks.AuthoringMaterializer.Build(
            "validate-rb",
            Munarium.Runbooks.AuthoringCatalog.Pattern("ask-the-corpus"),
            new Dictionary<string, object?>(StringComparer.Ordinal));

        var yaml = set!.Documents["runbooks/validate-rb.yaml"];
        var validated = await kernel.Operations.ValidateRunbookAsync(new WireRunbookValidationRequest(yaml));

        Assert.True(validated.Valid, string.Join(" | ", validated.Findings.Select(finding => $"{finding.Severity} {finding.Code}")));
        Assert.Empty(validated.Suggestions);

        // Asking for advice with no model bound says so rather than failing, and changes no finding.
        var advised = await kernel.Operations.ValidateRunbookAsync(new WireRunbookValidationRequest(yaml, Suggest: true));

        Assert.Equal(MunariumOperations.SuggestUnavailableNote, advised.SuggestNote);
        Assert.Equal(validated.Findings.Count, advised.Findings.Count);
    }
}