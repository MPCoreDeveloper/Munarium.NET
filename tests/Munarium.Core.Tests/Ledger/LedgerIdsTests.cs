namespace Munarium.Core.Tests.Ledger;

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
}
