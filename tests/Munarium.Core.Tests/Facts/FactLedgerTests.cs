namespace Munarium.Core.Tests.Facts;

using Munarium.Commands;
using Munarium.Core.Tests.Support;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Ledger;

/// <summary>
/// Tests for supersession and the <c>as_of</c> pin: what a pin saw stays what it saw, and the same
/// pin rebuilds the same digest.
/// </summary>
public class FactLedgerTests
{
    [Fact]
    public async Task APinShowsTheFactThatWasCurrentAtThatPin()
    {
        var storage = new FakeStorageBackend();
        var claims = new ClaimLedger(storage, VendorShape.Registry(), [new AlwaysPermitted()]);
        var facts = new FactLedger(storage);

        await claims.RecordAsync(Claim("north", "the supplier is north"));
        await claims.RecordAsync(Claim("north", "the supplier is north (revised)"));

        var atOne = await facts.SliceAsync(new SequenceNumber(1));
        var atTwo = await facts.SliceAsync(new SequenceNumber(2));

        Assert.Equal("the supplier is north", Assert.Single(atOne.Facts).Fact.Statement);
        Assert.Equal("the supplier is north (revised)", Assert.Single(atTwo.Facts).Fact.Statement);
    }

    [Fact]
    public async Task OneFactPerLineageSurvivesSupersession()
    {
        var storage = new FakeStorageBackend();
        var claims = new ClaimLedger(storage, VendorShape.Registry(), [new AlwaysPermitted()]);
        var facts = new FactLedger(storage);

        await claims.RecordAsync(Claim("north", "v1"));
        await claims.RecordAsync(Claim("south", "s1"));
        await claims.RecordAsync(Claim("north", "v2"));

        var slice = await facts.SliceAsync(new SequenceNumber(3));

        Assert.Equal(2, slice.Facts.Count);
        Assert.Equal(
            [VendorShape.Lineage("north"), VendorShape.Lineage("south")],
            slice.Facts.Select(fact => fact.Fact.Lineage));
        Assert.Equal("v2", slice.Facts[0].Fact.Statement);
    }

    [Fact]
    public async Task TheDigestIsStableForAPinAndALaterFactDoesNotChangeIt()
    {
        var storage = new FakeStorageBackend();
        var claims = new ClaimLedger(storage, VendorShape.Registry(), [new AlwaysPermitted()]);
        var facts = new FactLedger(storage);

        await claims.RecordAsync(Claim("north", "v1"));

        var first = await facts.SliceAsync(new SequenceNumber(1));
        var again = await facts.SliceAsync(new SequenceNumber(1));

        Assert.Equal(first.Digest, again.Digest);

        await claims.RecordAsync(Claim("north", "v2"));

        var stillPinned = await facts.SliceAsync(new SequenceNumber(1));
        var atTwo = await facts.SliceAsync(new SequenceNumber(2));

        // A correction does not rewrite history: the earlier pin still digests the same.
        Assert.Equal(first.Digest, stillPinned.Digest);
        Assert.NotEqual(first.Digest, atTwo.Digest);
    }

    [Fact]
    public async Task RebuildingTheSameFactsElsewhereProducesTheSameDigest()
    {
        Assert.Equal(await DigestOfRebuildAsync(), await DigestOfRebuildAsync());
    }

    [Fact]
    public async Task ABlockedFactCarriesItsVerdictIntoTheSlice()
    {
        var storage = new FakeStorageBackend();
        var claims = new ClaimLedger(storage, VendorShape.Registry(), [new BlocksEverything()]);
        var facts = new FactLedger(storage);

        await claims.RecordAsync(Claim("north", "the supplier is sanctioned"));

        var sliced = Assert.Single((await facts.SliceAsync(new SequenceNumber(1))).Facts);

        Assert.Equal("blocked:policy:not permitted", Describe(sliced.Verdict));
        Assert.True(sliced.Fact.IsDisputed);
    }

    private static async Task<string> DigestOfRebuildAsync()
    {
        var storage = new FakeStorageBackend();
        var claims = new ClaimLedger(storage, VendorShape.Registry(), [new AlwaysPermitted()]);
        var facts = new FactLedger(storage);

        await claims.RecordAsync(Claim("north", "v1"));
        await claims.RecordAsync(Claim("south", "s1"));
        await claims.RecordAsync(Claim("north", "v2"));

        return (await facts.SliceAsync(new SequenceNumber(3))).Digest;
    }

    private static RecordClaimCommand Claim(string vendorId, string statement) => new()
    {
        Stream = "claims/1",
        ClaimId = $"claim-{vendorId}",
        Shape = VendorShape.Name,
        Body = VendorShape.Body(vendorId),
        Statement = statement,
        Actor = "tester",
    };

    private static string Describe(ClaimVerdict verdict) => verdict switch
    {
        Permitted => "permitted",
        Blocked blocked => $"blocked:{blocked.Gate}:{blocked.Reason}",
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
