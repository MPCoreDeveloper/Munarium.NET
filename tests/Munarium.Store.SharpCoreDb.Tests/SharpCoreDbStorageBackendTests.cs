namespace Munarium.Store.SharpCoreDb.Tests;

using Microsoft.Extensions.DependencyInjection;
using Munarium.Ledger;
using SharpCoreDB;
using SharpCoreDB.EventSourcing;

/// <summary>
/// Tests for <see cref="SharpCoreDbStorageBackend"/>: the Munarium ledger seam over SharpCoreDB.
/// </summary>
public class SharpCoreDbStorageBackendTests
{
    [Fact]
    public async Task HeadAsyncOnAnEmptyStreamReturnsZero()
    {
        var backend = new SharpCoreDbStorageBackend(new InMemoryEventStore());

        var head = await backend.HeadAsync(StreamId.From("claim/1"));

        Assert.Equal(SequenceNumber.Zero, head);
    }

    [Fact]
    public async Task AppendAsyncWithNoStreamExpectedWritesAndReportsTheNewHead()
    {
        var backend = new SharpCoreDbStorageBackend(new InMemoryEventStore());

        var outcome = await backend.AppendAsync(
            StreamId.From("claim/1"),
            SequenceNumber.Zero,
            [LedgerEvent.FromText("claim.recorded", "the supplier is north")]);

        Assert.Equal("appended:1", Describe(outcome));
    }

    [Fact]
    public async Task AppendAsyncWithAStaleHeadIsRefusedAndWritesNothing()
    {
        var backend = new SharpCoreDbStorageBackend(new InMemoryEventStore());
        var stream = StreamId.From("claim/2");

        await backend.AppendAsync(stream, SequenceNumber.Zero, [LedgerEvent.FromText("claim.recorded", "north")]);
        var stale = await backend.AppendAsync(stream, SequenceNumber.Zero, [LedgerEvent.FromText("claim.recorded", "south")]);

        Assert.Equal("conflict:0->1", Describe(stale));
        Assert.Equal(new SequenceNumber(1), await backend.HeadAsync(stream));
    }

    [Fact]
    public async Task AppendAsyncWithTheCurrentHeadExtendsTheStreamInOrder()
    {
        var backend = new SharpCoreDbStorageBackend(new InMemoryEventStore());
        var stream = StreamId.From("claim/3");

        await backend.AppendAsync(stream, SequenceNumber.Zero, [LedgerEvent.FromText("claim.recorded", "v1")]);

        var outcome = await backend.AppendAsync(
            stream,
            new SequenceNumber(1),
            [LedgerEvent.FromText("claim.superseded", "v2")]);

        Assert.Equal("appended:2", Describe(outcome));
        Assert.Equal(new SequenceNumber(2), await backend.HeadAsync(stream));
    }

    [Fact]
    public async Task AppendAsyncAgainstThePersistentStoreHoldsTheExpectedHeadContract()
    {
        var services = new ServiceCollection();
        services.AddSharpCoreDB();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<DatabaseFactory>();
        var databasePath = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");
        await using var database = factory.Create(databasePath, "munarium-test");

        var backend = new SharpCoreDbStorageBackend(new SharpCoreDbEventStore(database));
        var stream = StreamId.From("claim/persistent");

        var first = await backend.AppendAsync(stream, SequenceNumber.Zero, [LedgerEvent.FromText("claim.recorded", "north")]);
        var replay = await backend.AppendAsync(stream, SequenceNumber.Zero, [LedgerEvent.FromText("claim.recorded", "south")]);
        var next = await backend.AppendAsync(stream, new SequenceNumber(1), [LedgerEvent.FromText("claim.superseded", "north revised")]);

        Assert.Equal("appended:1", Describe(first));
        Assert.Equal("conflict:0->1", Describe(replay));
        Assert.Equal("appended:2", Describe(next));
        Assert.Equal(new SequenceNumber(2), await backend.HeadAsync(stream));
    }

    private static string Describe(AppendOutcome outcome) => outcome switch
    {
        Appended appended => $"appended:{appended.Head.Value}",
        VersionConflict conflict => $"conflict:{conflict.Expected.Value}->{conflict.Actual.Value}",
    };
}
