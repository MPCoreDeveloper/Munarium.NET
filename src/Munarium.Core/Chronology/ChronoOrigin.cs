namespace Munarium.Chronology;

/// <summary>
/// Where a timeline event came from: the ledger's accepted facts, or the candidate being judged.
/// </summary>
/// <remarks>
/// The distinction is load-bearing rather than descriptive - it is what makes a violation attributable.
/// A candidate is laid over the ledger in the same timeline, so a correction that fixes a date clears
/// the violation in one evaluation instead of needing a second write.
/// </remarks>
public enum ChronoOrigin
{
    /// <summary>An accepted fact in the pinned snapshot.</summary>
    Ledger = 0,

    /// <summary>A claim proposed by the candidate under review.</summary>
    Candidate = 1,
}

/// <summary>
/// The wire names of <see cref="ChronoOrigin"/>, which travel inside a violation's chain.
/// </summary>
public static class ChronoOriginExtensions
{
    /// <summary>
    /// Returns the name the wire carries for an origin.
    /// </summary>
    /// <param name="origin">The origin.</param>
    /// <returns><c>ledger</c> or <c>candidate</c>.</returns>
    public static string ToWireName(this ChronoOrigin origin) =>
        origin is ChronoOrigin.Candidate ? "candidate" : "ledger";
}
