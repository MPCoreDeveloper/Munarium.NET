namespace Munarium.Core.Tests.Claims;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Governance;
using Munarium.Governance.Gates;
using Munarium.Ledger;

/// <summary>
/// Tests for the anchor write path: a lock is recorded, the plane carries it, and a release either lands or says
/// there was nothing to release.
/// </summary>
public class AnchorLedgerTests
{
    [Fact]
    public async Task ALockedDetailAppearsInThePlaneWithItsPosition()
    {
        var (ledger, _, snapshots) = Ledger();

        var locked = Locked(await ledger.LockAsync("release-1", "service.api_version", "v2", lockedAtScope: "release"));

        Assert.True(LedgerIds.IsLedgerId(locked.Id));
        Assert.Equal(1, locked.Sequence.Value);
        Assert.Equal(AnchorStatus.Locked, locked.Status);

        var anchor = (await snapshots.BuildAsync("release-1")).Anchors["service.api_version"];

        Assert.Equal("v2", anchor.LockedValue);
        Assert.Equal("release", anchor.LockedAtScope);
    }

    /// <summary>
    /// A lock that lost the head is retried and stamps the position it actually settled at: a position a write did
    /// not land on is a position no pin can see.
    /// </summary>
    [Fact]
    public async Task ALockLostToOneConcurrentWriterIsRetriedRatherThanMisStamped()
    {
        var (ledger, storage, snapshots) = Ledger();
        storage.FailNextAppendsWithConflict(1);

        var locked = Locked(await ledger.LockAsync("release-1", "service.api_version", "v2"));

        Assert.Equal(2, storage.AppendCalls);
        Assert.Equal(1, locked.Sequence.Value);
        Assert.Equal(1, locked.Sequence.Value);
        Assert.Single((await snapshots.BuildAsync("release-1")).Anchors);
    }

    [Fact]
    public async Task ALockThatLosesEveryAttemptIsReportedRatherThanWritten()
    {
        var (ledger, storage, snapshots) = Ledger(maxAttempts: 2);
        storage.FailNextAppendsWithConflict(2);

        var contended = Contended(await ledger.LockAsync("release-1", "service.api_version", "v2"));

        Assert.Equal(0, contended.Actual.Value);
        Assert.Empty((await snapshots.BuildAsync("release-1")).Anchors);
    }

    [Fact]
    public async Task ReleasingALockDropsItFromThePlane()
    {
        var (ledger, _, snapshots) = Ledger();
        await ledger.LockAsync("release-1", "service.api_version", "v2");

        var released = Released(await ledger.ReleaseAsync("release-1", "service.api_version"));

        Assert.Equal(AnchorStatus.Released, released.Status);
        Assert.Empty((await snapshots.BuildAsync("release-1")).Anchors);
    }

    [Fact]
    public async Task ReleasingSomethingNobodyLockedIsNotAWrite()
    {
        var (ledger, storage, _) = Ledger();

        var missing = NotLocked(await ledger.ReleaseAsync("release-1", "service.api_version"));

        Assert.Equal("service.api_version", missing.DetailKey);
        Assert.Equal(0, storage.AppendCalls);
    }

    [Fact]
    public async Task ADetailKeyThatNamesNoPropertyCannotBeLocked()
    {
        var (ledger, _, _) = Ledger();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ledger.LockAsync("release-1", "service", "v2"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ledger.ReleaseAsync("release-1", "service"));
    }

    /// <summary>
    /// The point of a lock: a claim that contradicts it is refused, and the finding names the anchor rather than
    /// reading as a ledger conflict it is not.
    /// </summary>
    [Fact]
    public async Task AClaimThatContradictsALockIsRefusedByTheAnchorGate()
    {
        var storage = new FakeStorageBackend();
        var snapshots = new MeshSnapshotBuilder(storage);

        await new AnchorLedger(storage, snapshots).LockAsync("release-1", "service.api_version", "v2");

        var outcome = Recorded(await new CandidateLedger(storage, snapshots).AppendAsync(
            "release-1",
            [CandidateFixture.Proposal("service", "api_version", "v3")]));

        Assert.Equal(ClaimStatus.Disputed, Assert.Single(outcome.Claims).Status);
        Assert.Contains(outcome.Findings, finding => finding.RuleId == AnchorConsistency.RuleId);
    }

    private static (AnchorLedger Ledger, FakeStorageBackend Storage, MeshSnapshotBuilder Snapshots) Ledger(
        int maxAttempts = 3)
    {
        var storage = new FakeStorageBackend();
        var snapshots = new MeshSnapshotBuilder(storage);

        return (new AnchorLedger(storage, snapshots, maxAttempts), storage, snapshots);
    }

    private static Anchor Locked(AnchorOutcome outcome) =>
        outcome is Anchor anchor ? anchor : throw new InvalidOperationException("The lock did not land.");

    private static WriteContended Contended(AnchorOutcome outcome) =>
        outcome is WriteContended contended ? contended : throw new InvalidOperationException("The lock landed.");

    private static Anchor Released(AnchorReleaseOutcome outcome) =>
        outcome is Anchor anchor ? anchor : throw new InvalidOperationException("The release did not land.");

    private static AnchorNotLocked NotLocked(AnchorReleaseOutcome outcome) =>
        outcome is AnchorNotLocked missing ? missing : throw new InvalidOperationException("Something was released.");

    private static CandidateRecorded Recorded(CandidateOutcome outcome) =>
        outcome is CandidateRecorded recorded ? recorded : throw new InvalidOperationException("The batch did not land.");
}
