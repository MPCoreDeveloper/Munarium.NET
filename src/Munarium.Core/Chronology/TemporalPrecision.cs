namespace Munarium.Chronology;

/// <summary>
/// How precisely a calendar assertion was made: a day, a whole month, or a whole year.
/// </summary>
/// <remarks>
/// Precision is carried rather than widened away, because it decides what may be concluded. "2020"
/// and "2020-05" are not in conflict and neither is before the other - the year contains the month -
/// so a rule that ignored precision would file a violation about a date that was never asserted.
/// </remarks>
public enum TemporalPrecision
{
    /// <summary>A single day.</summary>
    Day = 0,

    /// <summary>A whole month.</summary>
    Month = 1,

    /// <summary>A whole year.</summary>
    Year = 2,
}

/// <summary>
/// The wire names of <see cref="TemporalPrecision"/>, which travel with a finding's detail.
/// </summary>
public static class TemporalPrecisionExtensions
{
    /// <summary>
    /// Returns the name the wire carries for a precision.
    /// </summary>
    /// <param name="precision">The precision.</param>
    /// <returns><c>day</c>, <c>month</c> or <c>year</c>.</returns>
    public static string ToWireName(this TemporalPrecision precision) => precision switch
    {
        TemporalPrecision.Day => "day",
        TemporalPrecision.Month => "month",
        _ => "year",
    };
}
