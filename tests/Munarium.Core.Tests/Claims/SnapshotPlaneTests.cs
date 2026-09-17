namespace Munarium.Core.Tests.Claims;

using System.Text;
using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Governance.Gates;
using Munarium.Ledger;

/// <summary>
/// Tests for the snapshot planes that share the claims' stream: anchors and promises, their canonical
/// payloads, and the pin semantics the fold is supposed to deliver.
/// </summary>
public class SnapshotPlaneTests
{
    [Fact]
    public void AnAnchorRoundTripsAndTakesItsPositionFromTheStore()
    {
        var anchor = Anchor("service.api_version", "v1") with
        {
            LockedAtScope = "release.notes",
            EvidenceJson = """{"doc":"rfc-1"}""",
        };

        var decoded = AnchorCodec.Decode(AnchorCodec.Encode(anchor), new SequenceNumber(7));

        Assert.Equal(anchor.Id, decoded.Id);
        Assert.Equal("service.api_version", decoded.DetailKey);
        Assert.Equal("v1", decoded.LockedValue);
        Assert.Equal("release.notes", decoded.LockedAtScope);
        Assert.Equal("""{"doc":"rfc-1"}""", decoded.EvidenceJson);
        Assert.Equal(AnchorStatus.Locked, decoded.Status);

        // The payload cannot claim a position: the store assigns it and the read puts it back.
        Assert.Equal(new SequenceNumber(7), decoded.Sequence);
    }

    [Fact]
    public void AReleaseIsTheSameRecordWithTheReleasedStatus() =>
        Assert.Equal(
            AnchorStatus.Released,
            AnchorCodec
                .Decode(
                    AnchorCodec.Encode(Anchor("service.api_version", "v1") with { Status = AnchorStatus.Released }),
                    SequenceNumber.Zero)
                .Status);

    [Fact]
    public void APromiseRoundTripsAsRegisteredAndAsFulfilled()
    {
        var promise = Promise();

        var registered = PromiseCodec.DecodeRegistered(PromiseCodec.Encode(promise), new SequenceNumber(3));
        Assert.Equal("release.notes", registered.Key);
        Assert.Equal(PromiseStatus.Open, registered.Status);
        Assert.Null(registered.FulfilledSequence);

        var fulfilled = PromiseCodec.DecodeFulfilled(
            PromiseCodec.Encode(promise with { Status = PromiseStatus.Fulfilled }),
            new SequenceNumber(9));

        Assert.Equal(PromiseStatus.Fulfilled, fulfilled.Status);
        Assert.Equal(new SequenceNumber(9), fulfilled.FulfilledSequence);
    }

    [Theory]
    [InlineData("""{"anchor_id":"a","version_id":"v","detail_key":"d","locked_value":"x","status":9}""")]
    [InlineData("""{"anchor_id":"a","version_id":"v","detail_key":"d"}""")]
    [InlineData("""["not an object"]""")]
    public void AMalformedPlanePayloadIsRefused(string payload) =>
        Assert.Throws<FormatException>(() =>
            AnchorCodec.Decode(Encoding.UTF8.GetBytes(payload), SequenceNumber.Zero));

    [Fact]
    public async Task ALockedAnchorReachesTheSnapshotAndBlocksAContradiction()
    {
        var storage = new FakeStorageBackend();
        var builder = new MeshSnapshotBuilder(storage);
        await AppendAsync(storage, AnchorCodec.LockedEventType, AnchorCodec.Encode(Anchor("service.api_version", "v1")));

        var snapshot = await builder.BuildAsync("release-1");

        var anchor = Assert.Single(snapshot.Anchors.Values);
        Assert.Equal("service.api_version", anchor.DetailKey);
        Assert.Equal(new SequenceNumber(1), anchor.Sequence);

        // The payoff: the anchor gate now judges a claim against a lock that came out of storage.
        var findings = AnchorConsistency.Evaluate(
            snapshot,
            new Candidate { Claims = [CandidateFixture.Proposal("service", "api_version", "v2")] });

        Assert.Equal(AnchorConsistency.RuleId, Assert.Single(findings).RuleId);
    }

    /// <summary>
    /// A release recorded after the pin leaves the lock standing, exactly as a supersession recorded after the
    /// pin leaves the earlier value current.
    /// </summary>
    [Fact]
    public async Task AReleaseAfterThePinLeavesTheLockStanding()
    {
        var storage = new FakeStorageBackend();
        var builder = new MeshSnapshotBuilder(storage);
        await AppendAsync(storage, AnchorCodec.LockedEventType, AnchorCodec.Encode(Anchor("service.api_version", "v1")));
        await AppendAsync(
            storage,
            AnchorCodec.ReleasedEventType,
            AnchorCodec.Encode(Anchor("service.api_version", "v1") with { Status = AnchorStatus.Released }));

        Assert.Empty((await builder.BuildAsync("release-1")).Anchors);
        Assert.Single((await builder.BuildAsync("release-1", new SequenceNumber(1))).Anchors);
    }

    /// <summary>
    /// A promise fulfilled after the pin reads back open at the earlier pin - the semantic the promise
    /// registry documents, delivered here by the fold.
    /// </summary>
    [Fact]
    public async Task AFulfilmentAfterThePinReadsBackOpen()
    {
        var storage = new FakeStorageBackend();
        var builder = new MeshSnapshotBuilder(storage);
        await AppendAsync(storage, PromiseCodec.RegisteredEventType, PromiseCodec.Encode(Promise()));
        await AppendAsync(
            storage,
            PromiseCodec.FulfilledEventType,
            PromiseCodec.Encode(Promise() with { Status = PromiseStatus.Fulfilled }));

        var atHead = Assert.Single((await builder.BuildAsync("release-1")).Promises);
        Assert.Equal(PromiseStatus.Fulfilled, atHead.Status);
        Assert.Equal(new SequenceNumber(2), atHead.FulfilledSequence);

        var pinned = Assert.Single((await builder.BuildAsync("release-1", new SequenceNumber(1))).Promises);
        Assert.Equal(PromiseStatus.Open, pinned.Status);
        Assert.Null(pinned.FulfilledSequence);
    }

    /// <summary>A promise restated is the same promise, because the fold keys on its coordination key.</summary>
    [Fact]
    public async Task APromiseRestatedIsTheSamePromise()
    {
        var storage = new FakeStorageBackend();
        var builder = new MeshSnapshotBuilder(storage);
        await AppendAsync(storage, PromiseCodec.RegisteredEventType, PromiseCodec.Encode(Promise()));
        await AppendAsync(
            storage,
            PromiseCodec.RegisteredEventType,
            PromiseCodec.Encode(Promise() with { Description = "the payoff, restated" }));

        var promise = Assert.Single((await builder.BuildAsync("release-1")).Promises);
        Assert.Equal("the payoff, restated", promise.Description);
    }

    /// <summary>
    /// An unbudgeted counter and a counter held to zero are different statements, so the payload says which
    /// one it is rather than leaving the absence to mean both.
    /// </summary>
    [Fact]
    public void ACounterRoundTripsWithAndWithoutABudget()
    {
        var budgeted = CounterCodec.Decode(CounterCodec.Encode(new CounterTotal { Key = "flashback", Total = 3, Budget = 0 }));
        var unbudgeted = CounterCodec.Decode(CounterCodec.Encode(new CounterTotal { Key = "storm", Total = 9 }));

        Assert.Equal(3UL, budgeted.Total);
        Assert.Equal(0UL, budgeted.Budget);
        Assert.Equal("storm", unbudgeted.Key);
        Assert.Null(unbudgeted.Budget);
    }

    [Fact]
    public void AnEntityRoundTripsWithItsAliasesAndItsMerge()
    {
        var entity = new Entity
        {
            Id = "entity-7",
            VersionId = "release-1",
            CanonicalName = "Northern Supplies Ltd",
            EntityType = "vendor",
            Aliases = ["north supplies", "Northern Supply"],
            MergedInto = "entity-2",
            Sequence = SequenceNumber.Zero,
        };

        var decoded = EntityCodec.Decode(EntityCodec.Encode(entity), new SequenceNumber(5));

        Assert.Equal("Northern Supplies Ltd", decoded.CanonicalName);
        Assert.Equal("vendor", decoded.EntityType);
        // Aliases come back in ordinal order: which order they were seen in is not part of what an entity is,
        // so a payload depends on the set and not on the path that built it.
        Assert.Equal(["Northern Supply", "north supplies"], decoded.Aliases);
        Assert.Equal("entity-2", decoded.MergedInto);
        Assert.Equal(new SequenceNumber(5), decoded.Sequence);
    }

    /// <summary>
    /// The last event for a key is the state, and a counter recorded after the pin is not there yet: a plane
    /// is a state at a pin, not a log.
    /// </summary>
    [Fact]
    public async Task TheFoldKeepsTheLatestEventPerKey()
    {
        var storage = new FakeStorageBackend();
        var builder = new MeshSnapshotBuilder(storage);
        await AppendAsync(storage, CounterCodec.RecordedEventType, CounterCodec.Encode(new CounterTotal { Key = "flashback", Total = 3, Budget = 2 }));
        await AppendAsync(storage, CounterCodec.RecordedEventType, CounterCodec.Encode(new CounterTotal { Key = "flashback", Total = 5, Budget = 2 }));

        var counter = Assert.Single((await builder.BuildAsync("release-1")).Counters);
        Assert.Equal(5UL, counter.Total);

        // And at the earlier pin the total that was recorded then is what stands.
        Assert.Equal(3UL, Assert.Single((await builder.BuildAsync("release-1", new SequenceNumber(1))).Counters).Total);
    }

    private static async Task AppendAsync(FakeStorageBackend storage, string eventType, byte[] payload)
    {
        var stream = StreamId.From("release-1");
        var head = await storage.HeadAsync(stream);

        await storage.AppendAsync(stream, head, [new LedgerEvent(eventType, payload)]);
    }

    private static Anchor Anchor(string detailKey, string lockedValue) => new()
    {
        Id = $"anchor-{detailKey}",
        VersionId = "release-1",
        DetailKey = detailKey,
        LockedValue = lockedValue,
        Sequence = SequenceNumber.Zero,
    };

    private static Promise Promise() => new()
    {
        Id = "promise-1",
        VersionId = "release-1",
        Key = "release.notes",
        Kind = "setup",
        Description = "the payoff for the setup",
        OriginScope = "release.notes",
        DueScope = "release.notes",
        Sequence = SequenceNumber.Zero,
    };
}

