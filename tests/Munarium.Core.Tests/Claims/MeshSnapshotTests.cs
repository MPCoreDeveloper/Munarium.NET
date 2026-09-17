namespace Munarium.Core.Tests.Claims;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Ledger;

/// <summary>
/// Tests for the snapshot's own time: the instant it derives from the identities it carries.
/// </summary>
public class MeshSnapshotTests
{
    [Fact]
    public void TheSnapshotIsAsOldAsItsNewestIdentity()
    {
        var snapshot = ClaimFixture.Snapshot(
            ClaimFixture.Create(LedgerIds.NewAt(Instant(2026, 1, 1)), 1, "hero", "eyes", "green"),
            ClaimFixture.Create(LedgerIds.NewAt(Instant(2026, 6, 1)), 2, "hero", "home", "harbor"));

        Assert.Equal(Instant(2026, 6, 1), snapshot.WrittenAt);
        Assert.Equal(new DateOnly(2026, 6, 1), snapshot.WrittenOn);
    }

    [Fact]
    public void TheOrderTheIdentitiesAppearInDoesNotMatter()
    {
        Claim[] facts =
        [
            ClaimFixture.Create(LedgerIds.NewAt(Instant(2026, 1, 1)), 1, "hero", "eyes", "green"),
            ClaimFixture.Create(LedgerIds.NewAt(Instant(2026, 6, 1)), 2, "hero", "home", "harbor"),
        ];

        Assert.Equal(
            ClaimFixture.Snapshot(facts).WrittenAt,
            ClaimFixture.Snapshot([.. facts.Reverse()]).WrittenAt);
    }

    /// <summary>Every plane that has identities contributes: an anchor's lock time is a write too.</summary>
    [Fact]
    public void TheAnchorsPromisesAndEntitiesCarryTheirInstantsToo()
    {
        var snapshot = new MeshSnapshot
        {
            Anchors = new Dictionary<string, Anchor>(StringComparer.Ordinal)
            {
                ["hero.eyes"] = new()
                {
                    Id = LedgerIds.NewAt(Instant(2026, 8, 1)),
                    VersionId = "v1",
                    DetailKey = "hero.eyes",
                    LockedValue = "green",
                    Sequence = new SequenceNumber(1),
                },
            },
        };

        Assert.Equal(Instant(2026, 8, 1), snapshot.WrittenAt);
    }

    /// <summary>
    /// A caller may name its own claims with opaque ids - the original does - and then the snapshot
    /// carries no time at all, which is a state the callers that need one have to handle.
    /// </summary>
    [Fact]
    public void WithOnlyOpaqueIdentitiesThereIsNoInstant()
    {
        var snapshot = ClaimFixture.Snapshot(ClaimFixture.Create("c1", 1, "hero", "eyes", "green"));

        Assert.Null(snapshot.WrittenAt);
        Assert.Null(snapshot.WrittenOn);
    }

    private static DateTimeOffset Instant(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);
}
