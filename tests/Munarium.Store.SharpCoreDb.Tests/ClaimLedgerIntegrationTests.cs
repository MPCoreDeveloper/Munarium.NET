namespace Munarium.Store.SharpCoreDb.Tests;

using Microsoft.Extensions.DependencyInjection;
using Munarium.Commands;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Ledger;
using Munarium.Shapes;
using SharpCoreDB;
using SharpCoreDB.EventSourcing;

/// <summary>
/// End to end: governance on the command path, written to a real SharpCoreDB store, read back as a
/// fact slice at a pin.
/// </summary>
public class ClaimLedgerIntegrationTests
{
    [Fact]
    public async Task AnAssertedADisputedAndAMalformedClaimAllLandInTheLedger()
    {
        var services = new ServiceCollection();
        services.AddSharpCoreDB();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<DatabaseFactory>();
        var databasePath = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");
        await using var database = factory.Create(databasePath, "munarium-test");

        var shapes = Registry();
        var backend = new SharpCoreDbStorageBackend(new SharpCoreDbEventStore(database));
        var ledger = new ClaimLedger(backend, shapes, [new ShapeGate(shapes), new SanctionsGate()]);
        var facts = new FactLedger(backend);
        var stream = StreamId.From("claims/eu");

        var permitted = await ledger.RecordAsync(Command("north", "Northern Supplies Ltd is an approved vendor"));
        var blocked = await ledger.RecordAsync(Command("south", "Northern Supplies Ltd trades with a sanctioned entity"));
        var malformed = await ledger.RecordAsync(
            Command("east", "Eastern Supplies Ltd is an approved vendor") with { Body = """{"vendor_id":"east"}""" });

        Assert.Equal("asserted:1", Describe(permitted));
        Assert.Equal("disputed:sanctions:listed party:2", Describe(blocked));
        Assert.Equal("disputed:shape:$: required property 'status' is missing.:3", Describe(malformed));

        var written = await backend.ReadAsync(stream, SequenceNumber.Zero);
        Assert.Equal(3, written.Count);
        Assert.Equal(FactCodec.AssertedEventType, written[0].Event.Type);
        Assert.Equal(FactCodec.DisputedEventType, written[1].Event.Type);
        Assert.Equal(FactCodec.DisputedEventType, written[2].Event.Type);

        // Pin on the last write, so the test does not assume where the store's global feed starts.
        var pin = written[^1].GlobalSequence;
        var slice = await facts.SliceAsync(pin);

        Assert.Equal(3, slice.Facts.Count);
        Assert.Equal(64, slice.Digest.Length);

        // A slice is ordered by lineage, so: east, north, south.
        Assert.Equal("blocked:shape:$: required property 'status' is missing.", Describe(slice.Facts[0].Verdict));
        Assert.Equal("permitted", Describe(slice.Facts[1].Verdict));
        Assert.Equal("blocked:sanctions:listed party", Describe(slice.Facts[2].Verdict));
    }

    private static string Describe(ClaimVerdict verdict) => verdict switch
    {
        Permitted => "permitted",
        Blocked blocked => $"blocked:{blocked.Gate}:{blocked.Reason}",
    };

    private static string Describe(ClaimOutcome outcome) => outcome switch
    {
        ClaimAsserted asserted => $"asserted:{asserted.Head.Value}",
        ClaimRecordedAsDisputed disputed => $"disputed:{disputed.Gate}:{disputed.Reason}:{disputed.Head.Value}",
        ClaimContended contended => $"contended:{contended.Expected.Value}->{contended.Actual.Value}",
    };

    private static ShapeRegistry Registry() => new([
        new FactShape
        {
            Name = "vendor",
            Version = 1,
            Identity = ["vendor_id"],
            Schema = """
                {
                  "type": "object",
                  "required": ["vendor_id", "status"],
                  "additionalProperties": false,
                  "properties": {
                    "vendor_id": { "type": "string", "minLength": 1 },
                    "status": { "type": "string", "enum": ["approved", "sanctioned"] }
                  }
                }
                """,
        },
    ]);

    private static RecordClaimCommand Command(string vendorId, string statement) => new()
    {
        VersionId = "claims/eu",
        ClaimId = $"claim-{vendorId}",
        Shape = "vendor",
        Body = $$"""{"vendor_id":"{{vendorId}}","status":"approved"}""",
        Statement = statement,
        Actor = "compliance",
    };

    /// <summary>A stand-in policy gate: a claim naming a sanctioned party is disputed.</summary>
    private sealed class SanctionsGate : IClaimGate
    {
        public string Name => "sanctions";

        public ValueTask<ClaimVerdict> EvaluateAsync(
            RecordClaimCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ClaimVerdict>(
                command.Statement.Contains("sanction", StringComparison.OrdinalIgnoreCase)
                    ? new Blocked("sanctions", "listed party")
                    : Permitted.Instance);
    }
}
