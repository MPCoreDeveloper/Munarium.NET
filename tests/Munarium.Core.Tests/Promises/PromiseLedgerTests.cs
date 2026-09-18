namespace Munarium.Core.Tests.Promises;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Ledger;
using Munarium.Promises;

/// <summary>
/// Tests for the promise write path: one is registered, and a fulfilment either settles an open promise or says
/// there was nothing open to settle.
/// </summary>
public class PromiseLedgerTests
{
    [Fact]
    public async Task ARegisteredPromiseIsOpenAndCarriesItsPosition()
    {
        var (ledger, _, snapshots) = Ledger();

        var registered = Registered(await ledger.RegisterAsync(
            "release-1",
            "audit-report",
            "deliverable",
            "an audit report for the release",
            originScope: "release",
            dueScope: "compliance"));

        Assert.True(LedgerIds.IsLedgerId(registered.Id));
        Assert.Equal(1, registered.Sequence.Value);
        Assert.Equal(PromiseStatus.Open, registered.Status);

        var promise = Assert.Single((await snapshots.BuildAsync("release-1")).Promises);

        Assert.Equal("deliverable", promise.Kind);
        Assert.Equal("compliance", promise.DueScope);
    }

    [Fact]
    public async Task FulfillingAPromiseRecordsWhereItSettled()
    {
        var (ledger, _, snapshots) = Ledger();
        await ledger.RegisterAsync("release-1", "audit-report", "deliverable", "an audit report");

        var fulfilled = Fulfilled(await ledger.FulfilAsync("release-1", "audit-report"));

        Assert.Equal(PromiseStatus.Fulfilled, fulfilled.Status);
        Assert.Equal(2, fulfilled.Sequence.Value);
        Assert.Equal(2, fulfilled.FulfilledSequence?.Value);

        // The plane agrees with the answer, which is what makes the fulfilment citable.
        var promise = Assert.Single((await snapshots.BuildAsync("release-1")).Promises);

        Assert.Equal(PromiseStatus.Fulfilled, promise.Status);
        Assert.Equal(2, promise.FulfilledSequence?.Value);
    }

    [Fact]
    public async Task FulfillingAKeyThatIsNotOpenSaysSoWithoutWriting()
    {
        var (ledger, storage, _) = Ledger();

        var missing = NotOpen(await ledger.FulfilAsync("release-1", "audit-report"));

        Assert.Equal("audit-report", missing.Key);
        Assert.Equal(0, storage.AppendCalls);
    }

    /// <summary>
    /// A promise is fulfilled once: a second fulfilment would record a settled obligation as settled again, and the
    /// plane would then say it was fulfilled twice.
    /// </summary>
    [Fact]
    public async Task FulfillingTwiceDoesNotFulfilTwice()
    {
        var (ledger, _, _) = Ledger();
        await ledger.RegisterAsync("release-1", "audit-report", "deliverable", "an audit report");

        await ledger.FulfilAsync("release-1", "audit-report");

        var second = NotOpen(await ledger.FulfilAsync("release-1", "audit-report"));

        Assert.Equal("audit-report", second.Key);
    }

    /// <summary>
    /// The plane is keyed by the coordination key and the later event for a key wins, so registering a key again
    /// restates the obligation rather than adding a second one - which is also why "fulfil this key" is unambiguous.
    /// Two obligations alive at once need two keys.
    /// </summary>
    [Fact]
    public async Task RegisteringAKeyAgainRestatesIt()
    {
        var (ledger, _, snapshots) = Ledger();

        await ledger.RegisterAsync("release-1", "audit-report", "deliverable", "an audit report");
        await ledger.RegisterAsync("release-1", "audit-report", "deliverable", "a signed audit report");

        var promise = Assert.Single((await snapshots.BuildAsync("release-1")).Promises);

        Assert.Equal("a signed audit report", promise.Description);
    }

    [Fact]
    public async Task APromiseThatLosesEveryAttemptIsReportedRatherThanWritten()
    {
        var (ledger, storage, snapshots) = Ledger(maxAttempts: 2);
        storage.FailNextAppendsWithConflict(2);

        var contended = Contended(await ledger.RegisterAsync("release-1", "audit-report", "deliverable", "an audit report"));

        Assert.Equal(0, contended.Actual.Value);
        Assert.Empty((await snapshots.BuildAsync("release-1")).Promises);
    }

    private static (PromiseLedger Ledger, FakeStorageBackend Storage, MeshSnapshotBuilder Snapshots) Ledger(
        int maxAttempts = 3)
    {
        var storage = new FakeStorageBackend();
        var snapshots = new MeshSnapshotBuilder(storage);

        return (new PromiseLedger(storage, snapshots, maxAttempts), storage, snapshots);
    }

    private static Promise Registered(PromiseOutcome outcome) =>
        outcome is Promise promise ? promise : throw new InvalidOperationException("The promise did not register.");

    private static WriteContended Contended(PromiseOutcome outcome) =>
        outcome is WriteContended contended ? contended : throw new InvalidOperationException("The promise registered.");

    private static Promise Fulfilled(FulfilOutcome outcome) =>
        outcome is Promise promise ? promise : throw new InvalidOperationException("Nothing was fulfilled.");

    private static PromiseNotOpen NotOpen(FulfilOutcome outcome) =>
        outcome is PromiseNotOpen missing ? missing : throw new InvalidOperationException("Something was fulfilled.");
}
