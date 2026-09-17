namespace Munarium.Core.Tests.Claims;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Facts;
using Munarium.Ledger;

/// <summary>
/// Tests for the projection that turns a stored fact into the claim the gates reason over.
/// </summary>
public class ClaimProjectionTests
{
    [Fact]
    public void AStoredTripleProjectsWithItsScopeAndGlobalPosition()
    {
        var fact = new FactRecord
        {
            ClaimId = "c1",
            VersionId = "release-1",
            ClaimType = ClaimType.Correction,
            Lineage = "service.api_version",
            Subject = "service",
            Key = "api_version",
            Value = "v2",
            ScopePath = "release.notes",
            SupersedesId = "c0",
            Confidence = 0.5,
            Body = string.Empty,
            Statement = string.Empty,
            Actor = string.Empty,
            Gate = string.Empty,
            Reason = string.Empty,
        };

        var claim = ClaimProjection.Of(fact, new SequenceNumber(42));

        Assert.Equal("service.api_version", claim.ClaimKey);
        Assert.Equal("v2", claim.Value);
        Assert.Equal("release.notes", claim.ScopePath);
        Assert.Equal(ClaimType.Correction, claim.ClaimType);
        Assert.Equal(ClaimStatus.Accepted, claim.Status);
        Assert.Equal("c0", claim.SupersedesId);
        Assert.Equal(0.5, claim.Confidence);

        // The sequence is the global position, because that is the axis a pin is on.
        Assert.Equal(new SequenceNumber(42), claim.Sequence);
    }

    /// <summary>
    /// A fact governance refused projects as disputed: the status is the ledger's own record of the
    /// verdict, not something a reader re-derives from the reason text.
    /// </summary>
    [Fact]
    public void ADisputedFactProjectsAsDisputed()
    {
        var fact = new FactRecord
        {
            ClaimId = "c1",
            VersionId = "release-1",
            ClaimType = ClaimType.Fact,
            Lineage = "service.api_version",
            Subject = "service",
            Key = "api_version",
            Value = "v2",
            Body = string.Empty,
            Statement = string.Empty,
            Actor = string.Empty,
            Gate = "gate.ledger-conflict",
            Reason = "conflicts with accepted canon",
        };

        var claim = ClaimProjection.Of(fact, SequenceNumber.Zero);

        Assert.Equal(ClaimStatus.Disputed, claim.Status);
        Assert.Equal(Provenance.Witnessed, claim.Provenance);
    }

    /// <summary>
    /// A snapshot built from the ledger carries the facts of the version it was asked for, projected, and
    /// the pin it was read at.
    /// </summary>
    [Fact]
    public async Task ASnapshotCarriesAVersionsFactsAtAPin()
    {
        var storage = new FakeStorageBackend();
        var facts = new FactLedger(storage);
        var ledger = new Munarium.Governance.CandidateLedger(storage, new MeshSnapshotBuilder(facts));

        await ledger.AppendAsync("release-1", [CandidateFixture.Proposal("service", "api_version", "v2")]);
        await ledger.AppendAsync("release-2", [CandidateFixture.Proposal("service", "owner_team", "platform")]);

        var snapshot = await new MeshSnapshotBuilder(facts).BuildAsync("release-1");

        var claim = Assert.Single(snapshot.Facts);
        Assert.Equal("service.api_version", claim.ClaimKey);

        // The pin is where the reader stood - the second write moved the feed to 2 - while the claim the
        // snapshot carries is release-1's first: a position in the ledger and a position in a stream are
        // not the same axis, which is why the two numbers differ.
        Assert.Equal(new SequenceNumber(2), snapshot.AsOfSequence);
        Assert.Equal(new SequenceNumber(1), snapshot.MaxSequence);
    }
}
