namespace Munarium.Core.Tests.Chronology;

using Munarium.Chronology;

/// <summary>
/// Tests for the calendar grammar: what it reads, what it refuses to read, and where it hedges.
/// </summary>
public class ChronologyGrammarTests
{
    [Fact]
    public void TheGrammarReadsEverySupportedForm()
    {
        Assert.Equal(
            (Day("2020-05-17"), Day("2020-05-17"), TemporalPrecision.Day, false),
            Describe("2020-05-17"));

        Assert.Equal(
            (Day("2020-05-01"), Day("2020-05-31"), TemporalPrecision.Month, false),
            Describe("2020-05"));

        Assert.Equal(
            (Day("1995-01-01"), Day("1995-12-31"), TemporalPrecision.Year, false),
            Describe("1995"));

        Assert.Equal(
            (Day("1990-01-01"), Day("1995-12-31"), TemporalPrecision.Year, false),
            Describe("1990-1995"));

        Assert.Equal(
            (Day("2020-03-01"), Day("2020-06-30"), TemporalPrecision.Month, false),
            Describe("March-June 2020"));

        Assert.Equal(Day("2020-03-05"), ChronologyGrammar.Parse("March 5, 2020")!.Value.Start);
        Assert.Equal(Day("2020-03-05"), ChronologyGrammar.Parse("5th March 2020")!.Value.Start);
        Assert.Equal(
            (Day("2020-09-01"), Day("2020-09-30"), TemporalPrecision.Month, false),
            Describe("Sept 2020"));
    }

    [Theory]
    [InlineData("circa 1943")]
    [InlineData("~March 2020")]
    [InlineData("approx. 1950")]
    [InlineData("about 1950")]
    [InlineData("around 1950")]
    public void AHedgeMarkerMakesTheAssertionUncertain(string text) =>
        Assert.True(ChronologyGrammar.Parse(text)!.Value.Uncertain);

    [Fact]
    public void ASeasonIsUncertainAndSpansItsMonths()
    {
        var spring = ChronologyGrammar.Parse("spring 2021")!.Value;

        Assert.True(spring.Uncertain);
        Assert.Equal((Day("2021-03-01"), Day("2021-05-31")), (spring.Start, spring.End));
    }

    /// <summary>Winter is the one season that leaves the year it is named for.</summary>
    [Fact]
    public void WinterCrossesTheYearBoundary()
    {
        var winter = ChronologyGrammar.Parse("winter 2020")!.Value;

        Assert.Equal((Day("2020-12-01"), Day("2021-02-28")), (winter.Start, winter.End));
    }

    [Theory]
    [InlineData("next tuesday")]
    [InlineData("2020-13")]
    [InlineData("1995-1990")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("February 30, 2020")]
    [InlineData("sometime later")]
    public void TheGrammarNeverGuesses(string text) => Assert.Null(ChronologyGrammar.Parse(text));

    [Fact]
    public void TheCertaintyAlgebraDecidesOnlyWhatItCan()
    {
        var circa = ChronologyGrammar.Parse("circa 1943")!.Value;
        var year = ChronologyGrammar.Parse("1944")!.Value;

        // Uncertainty disables certainty entirely, in both directions.
        Assert.False(circa.DefinitelyBefore(year));
        Assert.False(year.DefinitelyAfter(circa));

        // Coarse against fine that could be consistent is undecided: the year contains the month.
        var coarseYear = ChronologyGrammar.Parse("2020")!.Value;
        var month = ChronologyGrammar.Parse("2020-05")!.Value;
        Assert.False(coarseYear.DefinitelyBefore(month));
        Assert.False(month.DefinitelyBefore(coarseYear));

        // Disjoint certain intervals decide.
        Assert.True(ChronologyGrammar.Parse("1990")!.Value.DefinitelyBefore(ChronologyGrammar.Parse("1995")!.Value));
    }

    [Theory]
    [InlineData(2024, 2, "2024-02-29")]
    [InlineData(2023, 2, "2023-02-28")]
    [InlineData(2020, 12, "2020-12-31")]
    public void TheLastDayOfAMonthIsItsRealLastDay(int year, int month, string expected) =>
        Assert.Equal(Day(expected), ChronologyGrammar.MonthEnd(year, month));

    private static (DateOnly Start, DateOnly End, TemporalPrecision Precision, bool Uncertain) Describe(string text)
    {
        var interval = ChronologyGrammar.Parse(text)!.Value;
        return (interval.Start, interval.End, interval.Precision, interval.Uncertain);
    }

    private static DateOnly Day(string value) => DateOnly.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
