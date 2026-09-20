namespace Munarium.Server.Tests;

using Munarium.Shapes;
using Munarium.Wire;

/// <summary>Tests for a draft as an author drives it: open it, answer it, and see what is still open.</summary>
public class AuthoringDraftTests
{
    /// <summary>A draft answers, reports its open questions, and hands back the interview its pattern asks for.</summary>
    [Fact]
    public async Task ADraftAnswersAndReportsWhatIsOpen()
    {
        await using var kernel = MunariumKernel.Create(
            Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}"),
            "munarium-drafts",
            new ShapeRegistry([]));

        var opened = Draft(await kernel.Operations.OpenDraftAsync(
            new WireAuthoringDraftRequest("vendor-security", "ask-the-corpus")));

        Assert.Empty(opened.Answers);
        Assert.Contains(opened.Todos, todo => todo.Contains("identity.description", StringComparison.Ordinal));
        Assert.Contains(opened.Sections, section => section.Id == "completion");
        Assert.Equal("identity", opened.Sections[0].Id);

        var answered = Draft(await kernel.Operations.AnswerDraftAsync(
            "vendor-security",
            new WireAuthoringAnswers(new Dictionary<string, WireAuthoringValue>(StringComparer.Ordinal)
            {
                ["identity.description"] = new WireAuthoringValue("Vendor security reviews.", null, null, null, null),
                ["retrieval.candidate_n"] = new WireAuthoringValue(null, 120L, null, null, null),
                ["access.uniform_public"] = new WireAuthoringValue(null, null, false, null, null),
                ["prefix.root"] = new WireAuthoringValue("vendors/", null, null, null, null),
                ["access.area_levels"] = new WireAuthoringValue(null, null, null, null, new Dictionary<string, WireAuthoringValue>(StringComparer.Ordinal)
                {
                    ["public"] = new WireAuthoringValue(null, 0L, null, null, null),
                    ["incidents"] = new WireAuthoringValue(null, 3L, null, null, null),
                }),
                ["prefix.areas"] = new WireAuthoringValue(null, null, null, new List<WireAuthoringValue>
                {
                    new(null, null, null, null, new Dictionary<string, WireAuthoringValue>(StringComparer.Ordinal)
                    {
                        ["path"] = new WireAuthoringValue("public/", null, null, null, null),
                    }),
                }, null),
            })));

        Assert.Equal("Vendor security reviews.", answered.Answers["identity.description"].Text);
        Assert.Equal(120L, answered.Answers["retrieval.candidate_n"].Number);
        Assert.Equal(false, answered.Answers["access.uniform_public"].Flag);
        Assert.Equal(3L, answered.Answers["access.area_levels"].Fields!["incidents"].Number);
        Assert.Equal("public/", answered.Answers["prefix.areas"].Items![0].Fields!["path"].Text);
        Assert.DoesNotContain(answered.Todos, todo => todo.Contains("identity.description", StringComparison.Ordinal));
        Assert.True(
            await kernel.Operations.ReadDraftAsync("nothing") is WireProblem problem && problem.Status == 404);

        var listed = await kernel.Operations.ListDraftsAsync();

        Assert.Single(listed.Drafts);
        Assert.Equal("vendor-security", listed.Drafts[0].Name);
    }

    /// <summary>Validation reports what a draft would apply, and removal happens once.</summary>
    [Fact]
    public async Task AValidationReportsAndARemovalHappensOnce()
    {
        await using var kernel = MunariumKernel.Create(
            Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}"),
            "munarium-drafts",
            new ShapeRegistry([]));

        // A draft with a pattern but no answers still has a document - a runbook full of placeholders - which is exactly
        // what an author wants to read before they have answered anything.
        Draft(await kernel.Operations.OpenDraftAsync(
            new WireAuthoringDraftRequest("vendor-security", "ask-the-corpus")));

        var validation = Validated(await kernel.Operations.ValidateDraftAsync("vendor-security"));

        // Validity is what the findings say and nothing else: a deployment that refused on a finding it never reported, or
        // reported one it did not refuse on, would leave an author guessing at what it wanted.
        Assert.Equal(!validation.Findings.Any(finding => finding.Severity == "error"), validation.Valid);

        Assert.All(
            validation.Findings,
            finding => Assert.True(
                finding.Severity is "error" or "warn" or "info",
                $"a finding of severity '{finding.Severity}' is not one this contract carries"));

        // What it still owes comes from the same rules that would materialize it, so an unanswered draft owes something.
        Assert.NotEmpty(validation.Todos);

        // A draft with no pattern is still a document: a runbook with nothing in it beyond its name, which validates
        // because there is nothing in it to be wrong. Refusing it would refuse the state every draft starts in.
        Draft(await kernel.Operations.OpenDraftAsync(new WireAuthoringDraftRequest("no-pattern")));

        // The helper enforces the type: a union boxes as itself, so IsType against a member does not work, and a pattern
        // match is what reads one.
        Assert.NotNull(Validated(await kernel.Operations.ValidateDraftAsync("no-pattern")));

        // A draft that is not kept here is not validated as though it were empty: it is refused.
        Assert.True(await kernel.Operations.ValidateDraftAsync("nothing") is WireProblem { Status: 404 });

        // Removal happens once: a second removal is not a second removal, it is a caller with a wrong idea about what
        // this deployment holds.
        Assert.True(
            await kernel.Operations.DeleteDraftAsync("vendor-security") is WireAuthoringDraftRemoved removed
            && removed.Name == "vendor-security");
        Assert.True(await kernel.Operations.ReadDraftAsync("vendor-security") is WireProblem { Status: 404 });
        Assert.True(await kernel.Operations.DeleteDraftAsync("vendor-security") is WireProblem { Status: 404 });
    }

    /// <summary>Reads a validation result, insisting it is one.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The validation.</returns>
    private static WireDraftValidation Validated(WireDraftValidationResult result) =>
        result is WireDraftValidation validation
            ? validation
            : throw new InvalidOperationException($"the validation was refused: {result}");

    /// <summary>Reads a draft result, insisting it is one.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The draft.</returns>
    private static WireAuthoringDraft Draft(WireAuthoringDraftResult result) =>
        result is WireAuthoringDraft draft
            ? draft
            : throw new InvalidOperationException($"the draft was refused: {result}");
}

