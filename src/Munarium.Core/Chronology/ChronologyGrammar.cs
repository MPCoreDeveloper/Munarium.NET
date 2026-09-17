namespace Munarium.Chronology;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// The canonical grammar a claim value is read as a calendar assertion with.
/// </summary>
/// <remarks>
/// The grammar is deliberately small and closed, and anything it does not cover returns
/// <see langword="null"/> rather than a best guess: a chronology gate that inferred a date from prose
/// would file findings about dates nobody asserted, and the first such finding would cost the operator
/// more than the gate saves. The supported forms are ISO dates, <c>YYYY-MM</c>, <c>YYYY</c>,
/// "Month YYYY", "Month D, YYYY", "D Month YYYY", year and month ranges, seasons, and the
/// <c>circa</c>/<c>approx</c>/<c>~</c> hedges.
/// <para>
/// The regular expressions are source-generated rather than compiled at run time, because compiling a
/// pattern is a runtime code path the NativeAOT promise does not want to depend on.
/// </para>
/// </remarks>
public static partial class ChronologyGrammar
{
    private const string MonthNames =
        "january|february|march|april|august|september|october|november|december|june|july|sept|jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec";

    private const string RangeSeparator = @"\s*(?:-|–|—|to)\s*";

    /// <summary>
    /// Reads a value as a calendar assertion.
    /// </summary>
    /// <param name="text">The asserted value.</param>
    /// <returns>The interval, or <see langword="null"/> when the grammar does not cover the text.</returns>
    public static TemporalInterval? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var cleaned = text.Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        var uncertain = false;
        var hedged = Circa().Replace(cleaned, string.Empty);
        if (!string.Equals(hedged, cleaned, StringComparison.Ordinal))
        {
            // A hedge marker is the actor saying the date is approximate, so it is carried rather than
            // resolved: every comparison against it is then undecided by construction.
            uncertain = true;
            cleaned = hedged.Trim();
            if (cleaned.Length == 0)
            {
                return null;
            }
        }

        return ParseUnhedged(cleaned, uncertain);
    }

    private static TemporalInterval? ParseUnhedged(string cleaned, bool uncertain)
    {
        if (IsoDay().Match(cleaned) is { Success: true } dayMatch)
        {
            var day = TryDate(Number(dayMatch, 1), Number(dayMatch, 2), Number(dayMatch, 3));
            return day is { } value ? new TemporalInterval(value, value, TemporalPrecision.Day, uncertain) : null;
        }

        if (IsoMonth().Match(cleaned) is { Success: true } monthMatch)
        {
            var year = Number(monthMatch, 1);
            var month = Number(monthMatch, 2);
            return month is >= 1 and <= 12 && TryDate(year, month, 1) is { } start
                ? new TemporalInterval(start, MonthEnd(year, month), TemporalPrecision.Month, uncertain)
                : null;
        }

        if (YearRange().Match(cleaned) is { Success: true } yearRangeMatch)
        {
            var from = Number(yearRangeMatch, 1);
            var to = Number(yearRangeMatch, 2);
            return from <= to && TryDate(from, 1, 1) is { } start && TryDate(to, 12, 31) is { } end
                ? new TemporalInterval(start, end, TemporalPrecision.Year, uncertain)
                : null;
        }

        if (YearOnly().Match(cleaned) is { Success: true } yearMatch)
        {
            var year = Number(yearMatch, 1);
            return TryDate(year, 1, 1) is { } start && TryDate(year, 12, 31) is { } end
                ? new TemporalInterval(start, end, TemporalPrecision.Year, uncertain)
                : null;
        }

        if (MonthRange().Match(cleaned) is { Success: true } monthRangeMatch)
        {
            var first = MonthNumber(monthRangeMatch.Groups[1].Value);
            var last = MonthNumber(monthRangeMatch.Groups[2].Value);
            var year = Number(monthRangeMatch, 3);

            return first is { } from
                && last is { } to
                && from <= to
                && TryDate(year, from, 1) is { } start
                    ? new TemporalInterval(start, MonthEnd(year, to), TemporalPrecision.Month, uncertain)
                    : null;
        }

        if (MonthDayYear().Match(cleaned) is { Success: true } monthDayMatch)
        {
            // "March 5, 2020": the month name, then the day, then the year.
            return Day(monthDayMatch, dayGroup: 2, monthGroup: 1, yearGroup: 3, uncertain);
        }

        if (DayMonthYear().Match(cleaned) is { Success: true } dayMonthMatch)
        {
            // "5th March 2020": the day, then the month name, then the year.
            return Day(dayMonthMatch, dayGroup: 1, monthGroup: 2, yearGroup: 3, uncertain);
        }

        if (MonthYear().Match(cleaned) is { Success: true } monthYearMatch)
        {
            var month = MonthNumber(monthYearMatch.Groups[1].Value);
            var year = Number(monthYearMatch, 2);

            return month is { } value && TryDate(year, value, 1) is { } start
                ? new TemporalInterval(start, MonthEnd(year, value), TemporalPrecision.Month, uncertain)
                : null;
        }

        return Season().Match(cleaned) is { Success: true } seasonMatch
            ? ParseSeason(seasonMatch.Groups[1].Value, Number(seasonMatch, 2))
            : null;
    }

    /// <summary>
    /// Resolves the last day of a month.
    /// </summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <returns>The last day of that month.</returns>
    public static DateOnly MonthEnd(int year, int month) =>
        month == 12
            ? new DateOnly(year, 12, 31)
            : new DateOnly(year, month + 1, 1).AddDays(-1);

    /// <summary>
    /// Resolves a month name to its number.
    /// </summary>
    /// <param name="name">The name, possibly abbreviated and in any case.</param>
    /// <returns>The month number, or <see langword="null"/> when the name is not a month.</returns>
    public static int? MonthNumber(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToLowerInvariant() switch
        {
            "january" or "jan" => 1,
            "february" or "feb" => 2,
            "march" or "mar" => 3,
            "april" or "apr" => 4,
            "may" => 5,
            "june" or "jun" => 6,
            "july" or "jul" => 7,
            "august" or "aug" => 8,
            "september" or "sep" or "sept" => 9,
            "october" or "oct" => 10,
            "november" or "nov" => 11,
            "december" or "dec" => 12,
            _ => null,
        };
    }

    private static TemporalInterval? ParseSeason(string name, int year)
    {
        var (first, last) = name.ToLowerInvariant() switch
        {
            "spring" => (3, 5),
            "summer" => (6, 8),
            "autumn" or "fall" => (9, 11),
            "winter" => (12, 2),
            _ => (0, 0),
        };

        if (first == 0 || TryDate(year, first, 1) is not { } start)
        {
            return null;
        }

        // Winter spans the year boundary, and a season is inherently hedged - it is a range of months
        // rather than a date - so it is uncertain whatever the text did or did not mark.
        var endYear = last < first ? year + 1 : year;
        return TryDate(endYear, last, 1) is not null
            ? new TemporalInterval(start, MonthEnd(endYear, last), TemporalPrecision.Month, Uncertain: true)
            : null;
    }

    private static int Number(Match match, int group) =>
        int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static TemporalInterval? Day(
        Match match,
        int dayGroup,
        int monthGroup,
        int yearGroup,
        bool uncertain)
    {
        var month = MonthNumber(match.Groups[monthGroup].Value);

        return month is { } resolved
            ? Date(Number(match, dayGroup), resolved, Number(match, yearGroup), uncertain)
            : null;
    }

    private static TemporalInterval? Date(int day, int month, int year, bool uncertain) =>
        TryDate(year, month, day) is { } value
            ? new TemporalInterval(value, value, TemporalPrecision.Day, uncertain)
            : null;

    private static DateOnly? TryDate(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;

    [GeneratedRegex(@"^(?:circa|ca\.?|c\.|~|approx(?:\.|imately)?|around|about)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Circa();

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})$")]
    private static partial Regex IsoDay();

    [GeneratedRegex(@"^(\d{4})-(\d{2})$")]
    private static partial Regex IsoMonth();

    [GeneratedRegex(@"^(\d{4})$")]
    private static partial Regex YearOnly();

    [GeneratedRegex(@"^(\d{4})" + RangeSeparator + @"(\d{4})$")]
    private static partial Regex YearRange();

    [GeneratedRegex(@"^(" + MonthNames + @")\.?\s+(\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex MonthYear();

    [GeneratedRegex(
        @"^(" + MonthNames + @")\.?" + RangeSeparator + @"(" + MonthNames + @")\.?\s+(\d{4})$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MonthRange();

    [GeneratedRegex(
        @"^(" + MonthNames + @")\.?\s+(\d{1,2})(?:st|nd|rd|th)?(?:,\s*|\s+)(\d{4})$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MonthDayYear();

    [GeneratedRegex(
        @"^(\d{1,2})(?:st|nd|rd|th)?\s+(" + MonthNames + @")\.?,?\s+(\d{4})$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DayMonthYear();

    [GeneratedRegex(@"^(spring|summer|autumn|fall|winter)\s+(\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex Season();
}
