namespace Munarium.Core.Tests.Ledger;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Ledger;
using Posseth.UlidFactory;

/// <summary>
/// Tests for the ledger's identities: that they carry the instant they were created at, and that
/// reading that instant is total.
/// </summary>
public class LedgerIdsTests
{
    [Fact]
    public void AGeneratedIdentityIsAUlid()
    {
        var id = LedgerIds.New();

        Assert.Equal(26, id.Length);
        Assert.True(LedgerIds.IsLedgerId(id));
        Assert.NotNull(LedgerIds.InstantOf(id));
    }

    /// <summary>
    /// An identity created for an explicit instant carries <em>that</em> instant, to the millisecond the
    /// format holds - which is what lets a backfilled import record when something happened rather than
    /// when the import ran.
    /// </summary>
    [Fact]
    public void AnIdentityCarriesTheInstantItWasCreatedFor()
    {
        var instant = new DateTimeOffset(2026, 9, 17, 12, 34, 56, 789, TimeSpan.Zero);

        Assert.Equal(instant, LedgerIds.InstantOf(LedgerIds.NewAt(instant)));
    }

    /// <summary>Sortable by time is the property the ledger depends on, so it is asserted rather than assumed.</summary>
    [Fact]
    public void IdentitiesCreatedLaterSortLater()
    {
        var earlier = LedgerIds.NewAt(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        var later = LedgerIds.NewAt(new DateTimeOffset(2026, 9, 17, 12, 0, 1, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    /// <summary>
    /// The instant is read in UTC, so an identity created at a local offset is dated by the moment, not by
    /// the caller's time zone - the date rules downstream must not move with the machine they run on.
    /// </summary>
    [Fact]
    public void TheInstantIsReadInUtc()
    {
        var local = new DateTimeOffset(2026, 9, 17, 1, 0, 0, TimeSpan.FromHours(2));

        var instant = LedgerIds.InstantOf(LedgerIds.NewAt(local));

        Assert.Equal(new DateTimeOffset(2026, 9, 16, 23, 0, 0, TimeSpan.Zero), instant);
    }

    [Theory]
    [InlineData("c1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-ulid-at-all-1234567")]
    public void AnIdentityThatIsNotAUlidIsNotClaimedToBeOne(string? value)
    {
        Assert.False(LedgerIds.IsLedgerId(value));
        Assert.Null(LedgerIds.InstantOf(value));
    }

    /// <summary>
    /// The format's 48 bits reach the year 10889, which no date type can hold. That identity carries no
    /// usable instant, and nothing is invented in its place.
    /// </summary>
    [Fact]
    public void AnUnrepresentableInstantReadsAsNone()
    {
        var farFuture = Ulid.NewUlid(0xFF_FF_FF_FF_FF_FF);

        Assert.True(LedgerIds.IsLedgerId(farFuture.Value));
        Assert.Null(LedgerIds.InstantOf(farFuture.Value));
    }

    [Fact]
    public void TheNewestInstantWinsAndTheRestAreIgnored()
    {
        var oldest = LedgerIds.NewAt(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newest = LedgerIds.NewAt(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            LedgerIds.NewestInstantOf([null, "c1", oldest, "", newest]));
    }

    [Fact]
    public void WithNoIdentityCarryingAnInstantThereIsNone() =>
        Assert.Null(LedgerIds.NewestInstantOf([null, "c1", "claim-7"]));

    /// <summary>
    /// The third thing an identity carries: an order. Identities handed out later sort later, whatever
    /// order they arrive in.
    /// </summary>
    [Fact]
    public void IdentitiesSortByTheInstantTheyCarry()
    {
        var first = LedgerIds.NewAt(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var second = LedgerIds.NewAt(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var third = LedgerIds.NewAt(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal([first, second, third], LedgerIds.InOrder([third, first, second]));
        Assert.True(LedgerIds.ChronologicalComparer.Compare(first, third) < 0);
        Assert.True(LedgerIds.ChronologicalComparer.Compare(third, first) > 0);
        Assert.Equal(0, LedgerIds.ChronologicalComparer.Compare(second, second));
    }

    /// <summary>
    /// Two identities from the same millisecond are ordered by their random tails: an order that is still
    /// a total one, so a sort is reproducible even when the clock cannot separate the two.
    /// </summary>
    [Fact]
    public void IdentitiesInsideOneMillisecondStillSortDeterministically()
    {
        var instant = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var left = LedgerIds.NewAt(instant);
        var right = LedgerIds.NewAt(instant);

        Assert.Equal(
            LedgerIds.ChronologicalComparer.Compare(left, right),
            -LedgerIds.ChronologicalComparer.Compare(right, left));
        Assert.Equal(LedgerIds.InOrder([left, right]), LedgerIds.InOrder([right, left]));
    }

    /// <summary>
    /// A mix of ULIDs and opaque identities is not a timeline, but it is still orderable: the opaque ones
    /// fall back to text order, which is a tie-break rather than a claim about time.
    /// </summary>
    [Fact]
    public void AMixedSetIsStillTotallyOrdered()
    {
        var ulid = LedgerIds.NewAt(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var ordered = LedgerIds.InOrder([ulid, "c2", null, "c1", ""]);

        Assert.Equal(3, ordered.Count);
        Assert.Equal(["c1", "c2"], ordered.Where(value => !LedgerIds.IsLedgerId(value)));
        Assert.Equal(ordered, LedgerIds.InOrder(ordered));
    }

    /// <summary>
    /// The boundary, stated as a test: the order an identity carries is a <em>time</em> order, and the
    /// ledger's order is the position the store assigned. A writer whose clock disagrees with the order
    /// its (or another writer's) writes landed in makes the two disagree, and then the ledger's order is
    /// what resolves - because the pin is expressed in it, and a pin that depended on a clock would give
    /// two machines two answers.
    /// </summary>
    [Fact]
    public void TheIdentityOrderIsATimeOrderAndTheLedgerOrderIsNot()
    {
        // The second write carries the earlier clock: written later, stamped earlier.
        var firstWrite = ClaimFixture.Create("c1", 1, "hero", "eyes", "green");
        firstWrite = firstWrite with { Id = LedgerIds.NewAt(Instant(2026, 6, 1, 12)) };

        var secondWrite = ClaimFixture.Create("c2", 2, "hero", "eyes", "blue");
        secondWrite = secondWrite with
        {
            Id = LedgerIds.NewAt(Instant(2026, 6, 1, 11)),
            SupersedesId = firstWrite.Id,
        };

        // By the identities, the first write is the newer one...
        Assert.Equal([secondWrite.Id, firstWrite.Id], LedgerIds.InOrder([firstWrite.Id, secondWrite.Id]));

        // ...and by the ledger, the second is, because it holds the higher position.
        var current = Assert.Single(ClaimResolution.Resolve([firstWrite, secondWrite]));
        Assert.Equal(secondWrite.Id, current.Id);
        Assert.Equal("blue", current.Value);
    }

    private static DateTimeOffset Instant(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
