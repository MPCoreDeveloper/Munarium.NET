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

    /// <summary>Reads a draft result, insisting it is one.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The draft.</returns>
    private static WireAuthoringDraft Draft(WireAuthoringDraftResult result) =>
        result is WireAuthoringDraft draft
            ? draft
            : throw new InvalidOperationException($"the draft was refused: {result}");
}