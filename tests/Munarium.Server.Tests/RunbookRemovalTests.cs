namespace Munarium.Server.Tests;

using Munarium.Shapes;
using Munarium.Wire;

/// <summary>Tests for removing an applied runbook version: two passes, and only the armer may confirm.</summary>
public class RunbookRemovalTests
{
    /// <summary>An armed removal keeps the version answering, and a wrong identity cannot confirm it.</summary>
    [Fact]
    public async Task AnArmedRemovalTakesTwoPasses()
    {
        await using var kernel = MunariumKernel.Create(
            Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}"),
            "munarium-runbook-removal",
            new ShapeRegistry([]),
            MunariumShapeBundles.Store(Path.Combine(Path.GetTempPath(), $"MunariumShapes_{Guid.NewGuid():N}")));

        Draft(await kernel.Operations.OpenDraftAsync(new WireAuthoringDraftRequest("removal-rb", "ask-the-corpus")));
        _ = await kernel.Operations.ApplyDraftAsync("removal-rb");

        var armed = Removal(await kernel.Operations.RequestRunbookRemovalAsync("removal-rb@1"));

        Assert.Equal("removal-rb@1", armed.RunbookRef);
        Assert.StartsWith("rm-", armed.RemovalId!, StringComparison.Ordinal);
        Assert.NotEqual("Removed", armed.Status);

        // Still there, because arming is not removing, and a session may still pin the version.
        Assert.Contains(
            (await kernel.Operations.ListRunbooksAsync()).Runbooks,
            runbook => runbook.RunbookRef == "removal-rb@1");

        // Confirming under another identity is refused rather than obeyed: a confirmation has to be about one request.
        Assert.True(
            await kernel.Operations.ConfirmRunbookRemovalAsync(
                "removal-rb@1",
                new WireRunbookRemovalRequest("rm-not-the-one")) is WireProblem { Status: 409 });

        var removed = Removal(await kernel.Operations.ConfirmRunbookRemovalAsync(
            "removal-rb@1",
            new WireRunbookRemovalRequest(armed.RemovalId!)));

        Assert.Equal("Removed", removed.Status);
        Assert.NotNull(removed.RemovedAt);

        // Removed versions are retained and listed, so the ledger of what was applied stays whole.
        Assert.Contains(
            (await kernel.Operations.ListRunbooksAsync(includeRemoved: true)).Runbooks,
            runbook => runbook.RunbookRef == "removal-rb@1");

        // Removing what is already removed is gone rather than missing, and what was never applied is missing.
        Assert.True(await kernel.Operations.RequestRunbookRemovalAsync("removal-rb@1") is WireProblem { Status: 410 });
        Assert.True(await kernel.Operations.RequestRunbookRemovalAsync("nothing@1") is WireProblem { Status: 404 });
    }

    /// <summary>Reads a removal, insisting it is one.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The removal.</returns>
    private static WireRunbookRemoval Removal(WireRunbookRemovalResult result) =>
        result is WireRunbookRemoval removal
            ? removal
            : throw new InvalidOperationException($"the removal was refused: {result}");

    /// <summary>Reads a draft, insisting it is one.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The draft.</returns>
    private static WireAuthoringDraft Draft(WireAuthoringDraftResult result) =>
        result is WireAuthoringDraft draft
            ? draft
            : throw new InvalidOperationException($"the draft was refused: {result}");
}