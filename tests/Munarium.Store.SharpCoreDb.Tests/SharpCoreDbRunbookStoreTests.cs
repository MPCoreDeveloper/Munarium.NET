namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Runbooks;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbRunbookStore"/>: one row per version, resolution by name and by reference, and the
/// two-pass removal that never deletes the YAML.
/// </summary>
public class SharpCoreDbRunbookStoreTests
{
    /// <summary>
    /// Every applied version is its own row: a reference resolves to exactly that version, a bare name to the newest one
    /// <em>numerically</em> - so version 10 is newer than 9 - and another tenant sees none of it.
    /// </summary>
    [Fact]
    public async Task VersionsResolveByReferenceAndByNameNewestFirst()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Runbooks.ApplyAsync(Record("northgate@9"));
        await fixture.Runbooks.ApplyAsync(Record("northgate@10"));
        await fixture.Runbooks.ApplyAsync(Record("southgate@1"));

        var exact = await fixture.Runbooks.GetAsync("acme", "northgate@9");

        Assert.Equal("northgate@9", exact?.Ref);
        Assert.Equal("northgate", exact?.Name);
        Assert.Equal(9, exact?.Version);
        Assert.Equal("kind: Runbook", exact?.Yaml);

        Assert.Equal("northgate@10", (await fixture.Runbooks.ResolveAsync("acme", "northgate"))?.Ref);
        Assert.Equal("northgate@9", (await fixture.Runbooks.ResolveAsync("acme", "northgate@9"))?.Ref);
        Assert.Equal("southgate@1", (await fixture.Runbooks.ResolveAsync("acme", "southgate"))?.Ref);
        Assert.Null(await fixture.Runbooks.ResolveAsync("acme", "nothing"));
        Assert.Null(await fixture.Runbooks.GetAsync("acme", "northgate@1"));
        Assert.Null(await fixture.Runbooks.GetAsync("other", "northgate@9"));

        var listed = await fixture.Runbooks.ListAsync("acme");

        Assert.Equal(["northgate@9", "northgate@10", "southgate@1"], listed.Select(record => record.Ref));
        Assert.All(listed, record => Assert.Equal(RunbookStatus.Active, record.Status));
    }

    /// <summary>
    /// Re-applying a version keeps when it was first applied and moves when it was last applied, because the two answer
    /// different questions: when the version appeared, and whether what is stored is what the operator last wrote.
    /// </summary>
    [Fact]
    public async Task ReApplyingKeepsTheCreationStampAndMovesTheUpdateStamp()
    {
        await using var fixture = SourceStoreFixture.Create();

        var first = await fixture.Runbooks.ApplyAsync(Record("northgate@1", yaml: "kind: Runbook\nv1"));
        var second = await fixture.Runbooks.ApplyAsync(Record("northgate@1", yaml: "kind: Runbook\nv2"));

        Assert.NotNull(first.CreatedAt);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.NotNull(second.UpdatedAt);
        Assert.Equal("kind: Runbook\nv2", (await fixture.Runbooks.GetAsync("acme", "northgate@1"))?.Yaml);
    }

    /// <summary>
    /// Removal takes two passes and never deletes: the first pass arms the row and leaves it usable, only the identity
    /// that armed it may confirm, and a removed version is hidden from every read that does not ask for it.
    /// </summary>
    [Fact]
    public async Task RemovalTakesTwoPassesAndOnlyTheArmedIdentityMayConfirm()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Runbooks.ApplyAsync(Record("northgate@1"));

        var armed = await fixture.Runbooks.RequestRemovalAsync(
            "acme",
            "northgate@1",
            "rm-1",
            "2026-09-18T00:00:00Z",
            "ada");

        Assert.Equal(RunbookStatus.RemoveRequested, armed?.Status);
        Assert.Equal("rm-1", armed?.RemovalId);
        Assert.Equal("ada", armed?.RemovalRequestedBy);

        // Still usable until it is confirmed.
        Assert.NotNull(await fixture.Runbooks.ResolveAsync("acme", "northgate"));

        // The wrong identity cannot remove it, and the version is still armed afterwards - not active, not removed.
        Assert.Null(await fixture.Runbooks.ConfirmRemovalAsync("acme", "northgate@1", "rm-2", "2026-09-18T00:01:00Z"));
        Assert.Equal(RunbookStatus.RemoveRequested, (await fixture.Runbooks.GetAsync("acme", "northgate@1"))?.Status);

        var removed = await fixture.Runbooks.ConfirmRemovalAsync("acme", "northgate@1", "rm-1", "2026-09-18T00:02:00Z");

        Assert.Equal(RunbookStatus.Removed, removed?.Status);
        Assert.Equal("2026-09-18T00:02:00Z", removed?.RemovedAt);

        // Hidden from every read that does not ask for it, and still readable - with its YAML - when one does.
        Assert.Null(await fixture.Runbooks.GetAsync("acme", "northgate@1"));
        Assert.Null(await fixture.Runbooks.ResolveAsync("acme", "northgate"));
        Assert.Empty(await fixture.Runbooks.ListAsync("acme"));

        var kept = await fixture.Runbooks.GetAsync("acme", "northgate@1", includeRemoved: true);

        Assert.Equal(RunbookStatus.Removed, kept?.Status);
        Assert.Equal("kind: Runbook", kept?.Yaml);
        Assert.Contains(kept?.Ref ?? string.Empty, (await fixture.Runbooks.ListAsync("acme", includeRemoved: true)).Select(record => record.Ref));

        // Nothing is left to remove, so a second request is refused rather than re-arming the row.
        Assert.Null(await fixture.Runbooks.RequestRemovalAsync(
            "acme",
            "northgate@1",
            "rm-3",
            "2026-09-18T00:03:00Z",
            "ada"));
    }

    /// <summary>
    /// Re-applying resets an in-flight removal, because the bytes changed: a removal armed against the previous content
    /// must not be able to remove the fresh version.
    /// </summary>
    [Fact]
    public async Task ReApplyingResetsAnInFlightRemoval()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Runbooks.ApplyAsync(Record("northgate@1"));
        await fixture.Runbooks.RequestRemovalAsync("acme", "northgate@1", "rm-1", "2026-09-18T00:00:00Z", "ada");

        var reapplied = await fixture.Runbooks.ApplyAsync(Record("northgate@1", yaml: "kind: Runbook\nfresh"));

        Assert.Equal(RunbookStatus.Active, reapplied.Status);
        Assert.Null(reapplied.RemovalId);
        Assert.Null(reapplied.RemovalRequestedAt);
        Assert.Null(reapplied.RemovalRequestedBy);

        // The stale removal can no longer remove anything.
        Assert.Null(await fixture.Runbooks.ConfirmRemovalAsync("acme", "northgate@1", "rm-1", "2026-09-18T00:05:00Z"));
        Assert.Equal(RunbookStatus.Active, (await fixture.Runbooks.GetAsync("acme", "northgate@1"))?.Status);
    }

    private static RunbookRecord Record(string runbookRef, string yaml = "kind: Runbook") => new()
    {
        Tenant = "acme",
        Ref = runbookRef,
        Yaml = yaml,
    };
}
