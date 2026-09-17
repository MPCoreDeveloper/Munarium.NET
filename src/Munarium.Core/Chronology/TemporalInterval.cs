namespace Munarium.Chronology;

using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// A calendar assertion as an interval, with its precision and whether it was hedged.
/// </summary>
/// <remarks>
/// The load-bearing distinction is <see cref="DefinitelyBefore"/>: it is true only when the comparison
/// is certain given both precisions and both hedge flags. "Circa 1943" is never definitely before or
/// after anything, and "2020" against "2020-05" is undecided - the year contains the month. Rules fire
/// only on the definite outcomes, so an intentionally approximate date is never a violation by itself.
/// <para>
/// The interval is closed at both ends, which is what makes the month and year cases expressible
/// without a second representation: a year is the interval from 1 January to 31 December.
/// </para>
/// </remarks>
/// <param name="Start">The first day the assertion covers.</param>
/// <param name="End">The last day the assertion covers.</param>
/// <param name="Precision">How precisely it was asserted.</param>
/// <param name="Uncertain">Whether it was hedged (<c>circa</c>, <c>approx</c>, or a season).</param>
public readonly record struct TemporalInterval(DateOnly Start, DateOnly End, TemporalPrecision Precision, bool Uncertain)
{
    /// <summary>Gets a value indicating whether the two intervals share a day.</summary>
    /// <param name="other">The other interval.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool Overlaps(TemporalInterval other) => Start <= other.End && other.Start <= End;

    /// <summary>
    /// Gets a value indicating whether this assertion is certainly earlier than another.
    /// </summary>
    /// <param name="other">The other interval.</param>
    /// <returns><see langword="true"/> only when neither side is hedged and the intervals are disjoint.</returns>
    public bool DefinitelyBefore(TemporalInterval other) =>
        !Uncertain && !other.Uncertain && End < other.Start;

    /// <summary>Gets a value indicating whether this assertion is certainly later than another.</summary>
    /// <param name="other">The other interval.</param>
    /// <returns><see langword="true"/> when the other is certainly before this one.</returns>
    public bool DefinitelyAfter(TemporalInterval other) => other.DefinitelyBefore(this);

    /// <summary>
    /// Projects the interval into the detail an operator reads.
    /// </summary>
    /// <returns>The interval as JSON, with ISO dates.</returns>
    public JsonObject ToDetail() => new()
    {
        ["start"] = Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["end"] = End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["precision"] = Precision.ToWireName(),
        ["uncertain"] = Uncertain,
    };
}
