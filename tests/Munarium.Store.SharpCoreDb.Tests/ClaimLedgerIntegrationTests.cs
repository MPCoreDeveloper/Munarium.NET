namespace Munarium.Store.SharpCoreDb.Tests;

using Microsoft.Extensions.DependencyInjection;
using Munarium.Commands;
using Munarium.Governance;
using Munarium.Ledger;
using SharpCoreDB;
using SharpCoreDB.EventSourcing;

/// <summary>
/// End to end: governance on the command path, written to a real SharpCoreDB store and read back.
/// </summary>
public class ClaimLedgerIntegrationTests
{
    [Fact]
    public async Task AnAssertedClaimAndADisputedClaimBothLandInTheLedger()
    {
        var services = new ServiceCollection();
        services.AddSharpCoreDB();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<DatabaseFactory>();
        var databasePath = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");
        await using var database = factory.Create(databasePath, "munarium-test");

        var backend = new SharpCoreDbStorageBackend(new SharpCoreDbEventStore(database));
        var ledger = new ClaimLedger(backend, [new SanctionsGate()]);
        var stream = StreamId.From("claims/eu");

        var permitted = await ledger.RecordAsync(Command(stream.Value, "Northern Supplies Ltd is an approved vendor"));
        var blocked = await ledger.RecordAsync(Command(stream.Value, "Northern Supplies Ltd trades with a sanctioned entity"));

        Assert.Equal("asserted:1", Describe(permitted));
        Assert.Equal("disputed:sanctions:listed party:2", Describe(blocked));

        var written = await backend.ReadAsync(stream, SequenceNumber.Zero);
        Assert.Equal(2, written.Count);
        Assert.Equal("claim.asserted", written[0].Type);
        Assert.Equal("claim.disputed", written[1].Type);
    }

    private static RecordClaimCommand Command(string stream, string statement) => new()
    {
        Stream = stream,
        ClaimId = "claim-1",
        Statement = statement,
        Actor = "compliance",
    };

    private static string Describe(ClaimOutcome outcome) => outcome switch
    {
        ClaimAsserted asserted => $"asserted:{asserted.Head.Value}",
        ClaimRecordedAsDisputed disputed => $"disputed:{disputed.Gate}:{disputed.Reason}:{disputed.Head.Value}",
        ClaimContended contended => $"contended:{contended.Expected.Value}->{contended.Actual.Value}",
    };

    /// <summary>A stand-in policy gate: a claim naming a sanctioned party is disputed.</summary>
    private sealed class SanctionsGate : IClaimGate
    {
        public string Name => "sanctions";

        public ValueTask<ClaimVerdict> EvaluateAsync(
            RecordClaimCommand command,
            CancellationToken cancellationToken = default)
        {
            if (command.Statement.Contains("sanction", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult<ClaimVerdict>(new Blocked("sanctions", "listed party"));
            }

            return ValueTask.FromResult<ClaimVerdict>(Permitted.Instance);
        }
    }
}
