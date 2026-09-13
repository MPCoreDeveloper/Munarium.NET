namespace Munarium.Core.Tests.Governance;

using Munarium.Commands;
using Munarium.Core.Tests.Support;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Ledger;
using SharpDispatch;

/// <summary>
/// Tests for the kernel's write path: governance on the command path, and a blocked claim that is
/// recorded rather than dropped.
/// </summary>
public class ClaimLedgerTests
{
    [Fact]
    public async Task APermittedClaimIsRecordedAsAsserted()
    {
        var storage = new FakeStorageBackend();
        var ledger = new ClaimLedger(storage, VendorShape.Registry(), [new AlwaysPermitted()]);

        var outcome = await ledger.RecordAsync(Claim("the supplier is north"));

        Assert.Equal("asserted:1", Describe(outcome));
        var written = await storage.ReadAsync(Stream("claims/1"), SequenceNumber.Zero);
        Assert.Single(written);
        Assert.Equal(FactCodec.AssertedEventType, written[0].Event.Type);
    }

    [Fact]
    public async Task ABlockedClaimIsRecordedAsDisputedRatherThanDropped()
    {
        var storage = new FakeStorageBackend();
        var ledger = new ClaimLedger(storage, VendorShape.Registry(), [new AlwaysPermitted(), new BlocksEverything()]);

        var outcome = await ledger.RecordAsync(Claim("the supplier is south"));

        Assert.Equal("disputed:policy:not permitted:1", Describe(outcome));

        // The point of the whole path: the refusal is in the ledger, not discarded.
        var written = await storage.ReadAsync(Stream("claims/1"), SequenceNumber.Zero);
        Assert.Single(written);
        Assert.Equal(FactCodec.DisputedEventType, written[0].Event.Type);
    }

    [Fact]
    public async Task AContendedWriteIsRetriedAgainstTheFreshHeadAndThenSucceeds()
    {
        var storage = new FakeStorageBackend();
        storage.FailNextAppendsWithConflict(1);
        var ledger = new ClaimLedger(storage, VendorShape.Registry(), [new AlwaysPermitted()]);

        var outcome = await ledger.RecordAsync(Claim("the supplier is north"));

        Assert.Equal("asserted:1", Describe(outcome));
        Assert.Equal(2, storage.AppendCalls);
    }

    [Fact]
    public async Task AWriteThatLosesEveryRetryIsReportedAsContended()
    {
        var storage = new FakeStorageBackend();
        storage.FailNextAppendsWithConflict(10);
        var ledger = new ClaimLedger(storage, VendorShape.Registry(), [new AlwaysPermitted()], maxAttempts: 2);

        var outcome = await ledger.RecordAsync(Claim("the supplier is north"));

        Assert.Equal("contended:0->0", Describe(outcome));
        Assert.Equal(2, storage.AppendCalls);
    }

    [Fact]
    public async Task TheDispatcherReportsABlockedClaimAsASuccessBecauseItWasRecorded()
    {
        var storage = new FakeStorageBackend();
        var ledger = new ClaimLedger(storage, VendorShape.Registry(), [new BlocksEverything()]);
        var dispatcher = new InMemoryCommandDispatcher();
        dispatcher.RegisterHandler<RecordClaimCommand>(new RecordClaimCommandHandler(ledger));

        var result = await dispatcher.DispatchAsync(Claim("the supplier is south"));

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("disputed by policy", result.Message, StringComparison.Ordinal);

        var written = await storage.ReadAsync(Stream("claims/1"), SequenceNumber.Zero);
        Assert.Single(written);
        Assert.Equal(FactCodec.DisputedEventType, written[0].Event.Type);
    }

    [Fact]
    public async Task TheLineageIsDerivedFromTheShapeNotSuppliedByTheCaller()
    {
        var storage = new FakeStorageBackend();
        var ledger = new ClaimLedger(storage, VendorShape.Registry(), [new AlwaysPermitted()]);

        await ledger.RecordAsync(Claim("the supplier is north"));

        // Supersession is a property of the shape, so the caller cannot get the lineage wrong.
        var written = await storage.ReadAsync(Stream("claims/1"), SequenceNumber.Zero);
        var fact = FactCodec.Decode(written[0].Event.Payload.Span);
        Assert.Equal("vendor@1|vendor_id=north", fact.Lineage);
    }

    private static StreamId Stream(string value) => StreamId.From(value);

    private static RecordClaimCommand Claim(string statement) => new()
    {
        Stream = "claims/1",
        ClaimId = "claim-1",
        Shape = VendorShape.Name,
        Body = VendorShape.Body("north"),
        Statement = statement,
        Actor = "tester",
    };

    private static string Describe(ClaimOutcome outcome) => outcome switch
    {
        ClaimAsserted asserted => $"asserted:{asserted.Head.Value}",
        ClaimRecordedAsDisputed disputed => $"disputed:{disputed.Gate}:{disputed.Reason}:{disputed.Head.Value}",
        ClaimContended contended => $"contended:{contended.Expected.Value}->{contended.Actual.Value}",
    };

    private sealed class AlwaysPermitted : IClaimGate
    {
        public string Name => "always";

        public ValueTask<ClaimVerdict> EvaluateAsync(
            RecordClaimCommand command,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ClaimVerdict>(Permitted.Instance);
    }

    private sealed class BlocksEverything : IClaimGate
    {
        public string Name => "policy";

        public ValueTask<ClaimVerdict> EvaluateAsync(
            RecordClaimCommand command,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ClaimVerdict>(new Blocked("policy", "not permitted"));
    }
}
