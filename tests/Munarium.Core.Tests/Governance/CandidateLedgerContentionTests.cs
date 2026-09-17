namespace Munarium.Core.Tests.Governance;

using Munarium.Core.Tests.Support;
using Munarium.Governance;
using Munarium.Ledger;

/// <summary>
/// Tests for the two properties that make a candidate write safe: a pinned head that no longer holds is a
/// contention, and an unpinned one that moved is re-gated rather than lost.
/// </summary>
public class CandidateLedgerContentionTests
{
    [Fact]
    public async Task ACallerPinThatNoLongerHoldsIsContendedAndWritesNothing()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v2")]);

        var outcome = await ledger.AppendAsync(
            "release-1",
            [CandidateFixture.Proposal("service", "owner_team", "platform")],
            expectedHead: SequenceNumber.Zero);

        var contended = Contended(outcome);
        Assert.Equal(SequenceNumber.Zero, contended.Expected);
        Assert.Equal(new SequenceNumber(1), contended.Actual);
        Assert.Equal(1, storage.AppendCalls);
    }

    [Fact]
    public async Task ACallerPinThatStillHoldsIsAppendedAt()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();

        var outcome = await ledger.AppendAsync(
            "release-1",
            [CandidateFixture.Proposal("service", "api_version", "v2")],
            expectedHead: SequenceNumber.Zero);

        Assert.True(outcome is CandidateRecorded);
        Assert.Equal(1, storage.AppendCalls);
    }

    /// <summary>
    /// Without a caller pin, a head that moved under the writer is not an error: the batch is judged again
    /// against the canon it will actually land beside, and the append is retried rather than lost.
    /// </summary>
    [Fact]
    public async Task AHeadThatMovesUnderTheWriterIsReGatedAndRetried()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        storage.FailNextAppendsWithConflict(1);

        var outcome = await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v2")]);

        var recorded = Recorded(outcome);
        Assert.Single(recorded.Claims);
        Assert.Equal(2, storage.AppendCalls);
    }

    /// <summary>
    /// The retry is bounded: a stream that keeps moving ends as a contention rather than as a spin.
    /// </summary>
    [Fact]
    public async Task AStreamThatKeepsMovingEndsAsAContention()
    {
        var (ledger, storage, _) = CandidateFixture.Ledger();
        storage.FailNextAppendsWithConflict(10);

        var outcome = await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v2")]);

        var contended = Contended(outcome);
        Assert.Equal(SequenceNumber.Zero, contended.Expected);
        Assert.Equal(3, storage.AppendCalls);
    }

    private static CandidateRecorded Recorded(CandidateOutcome outcome) =>
        outcome is CandidateRecorded recorded
            ? recorded
            : throw new InvalidOperationException("The batch did not land.");

    private static CandidateContended Contended(CandidateOutcome outcome) =>
        outcome is CandidateContended contended
            ? contended
            : throw new InvalidOperationException("The batch landed; there is no contention to report.");
}
