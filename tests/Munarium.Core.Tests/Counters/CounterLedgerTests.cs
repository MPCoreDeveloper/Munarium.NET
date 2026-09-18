namespace Munarium.Core.Tests.Counters;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Counters;

/// <summary>
/// Tests for the counter write path: a total is recorded, the later one wins, and the directives a writer is given
/// follow from whatever the plane holds.
/// </summary>
public class CounterLedgerTests
{
    [Fact]
    public async Task ARecordedTotalAppearsInThePlaneWithoutAPositionOfItsOwn()
    {
        var (ledger, _, snapshots) = Ledger();

        var recorded = Recorded(await ledger.RecordAsync("release-1", "the bell", total: 4, budget: 6));

        Assert.Equal(4ul, recorded.Total);
        Assert.Equal(6ul, recorded.Budget);

        var counter = Assert.Single((await snapshots.BuildAsync("release-1")).Counters);

        Assert.Equal("the bell", counter.Key);
        Assert.Equal(4ul, counter.Total);
    }

    /// <summary>
    /// The total is absolute, so recording again is an upsert rather than a second entry: a reader must not have to
    /// sum a stream to know how often a pattern was used.
    /// </summary>
    [Fact]
    public async Task RecordingATotalAgainReplacesIt()
    {
        var (ledger, _, snapshots) = Ledger();

        await ledger.RecordAsync("release-1", "the bell", total: 4, budget: 6);
        await ledger.RecordAsync("release-1", "the bell", total: 5);

        var counter = Assert.Single((await snapshots.BuildAsync("release-1")).Counters);

        Assert.Equal(5ul, counter.Total);

        // The later record said nothing about a ceiling, so there is none: the counter is a state, and the writer
        // that last moved it is the one whose statement stands.
        Assert.Null(counter.Budget);
    }

    /// <summary>
    /// The reason a counter exists: a writer that is not told about a budget can only fail it, so the usage line
    /// travels with the work and an exhausted budget becomes an instruction not to use the pattern at all.
    /// </summary>
    [Fact]
    public async Task TheDirectivesTellAWriterWhereItStands()
    {
        var (ledger, _, snapshots) = Ledger();

        await ledger.RecordAsync("release-1", "the bell", total: 4, budget: 6);
        await ledger.RecordAsync("release-1", "tapestry", total: 3, budget: 3);
        await ledger.RecordAsync("release-1", "unbudgeted", total: 99);

        var directives = CounterBudget.Directives((await snapshots.BuildAsync("release-1")).Counters);

        Assert.Contains("the bell: used 4/6", directives, StringComparison.Ordinal);
        Assert.Contains("tapestry: used 3/3", directives, StringComparison.Ordinal);
        Assert.Contains("AVOID: 'tapestry'", directives, StringComparison.Ordinal);
        Assert.DoesNotContain("unbudgeted", directives, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACounterThatIsOverItsBudgetIsReported()
    {
        var (ledger, _, snapshots) = Ledger();
        await ledger.RecordAsync("release-1", "the bell", total: 7, budget: 6);

        var finding = Assert.Single(CounterBudget.Findings((await snapshots.BuildAsync("release-1")).Counters, "ch4"));

        Assert.Equal(CounterBudget.RuleId, finding.RuleId);
        Assert.Equal(Severity.Warn, finding.Severity);
        Assert.Equal("ch4", finding.ScopePath);
    }

    [Fact]
    public async Task ARecordThatLosesEveryAttemptIsReportedRatherThanWritten()
    {
        var (ledger, storage, snapshots) = Ledger(maxAttempts: 2);
        storage.FailNextAppendsWithConflict(2);

        var contended = Contended(await ledger.RecordAsync("release-1", "the bell", total: 4));

        Assert.Equal(0, contended.Actual.Value);
        Assert.Empty((await snapshots.BuildAsync("release-1")).Counters);
    }

    [Fact]
    public async Task ARecordWithoutAKeyIsRefused()
    {
        var (ledger, storage, _) = Ledger();

        await Assert.ThrowsAsync<ArgumentException>(async () => await ledger.RecordAsync("release-1", " ", total: 1));

        Assert.Equal(0, storage.AppendCalls);
    }

    private static (CounterLedger Ledger, FakeStorageBackend Storage, MeshSnapshotBuilder Snapshots) Ledger(
        int maxAttempts = 3)
    {
        var storage = new FakeStorageBackend();
        var snapshots = new MeshSnapshotBuilder(storage);

        return (new CounterLedger(storage, maxAttempts), storage, snapshots);
    }

    private static CounterTotal Recorded(CounterOutcome outcome) =>
        outcome is CounterTotal counter ? counter : throw new InvalidOperationException("The count did not land.");

    private static WriteContended Contended(CounterOutcome outcome) =>
        outcome is WriteContended contended ? contended : throw new InvalidOperationException("The count landed.");
}
