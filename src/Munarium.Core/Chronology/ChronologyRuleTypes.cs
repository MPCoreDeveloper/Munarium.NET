namespace Munarium.Chronology;

/// <summary>
/// One ordering rule: the claim matching <see cref="Before"/> must come first.
/// </summary>
/// <param name="Before">The target that must come first.</param>
/// <param name="After">The target that must come later.</param>
public sealed record OrderRule(string Before, string After)
{
    /// <summary>Gets the severity the rule declares. Only <see cref="ChronologySeverity.Block"/> refuses.</summary>
    public string Severity { get; init; } = ChronologySeverity.Default;
}

/// <summary>
/// One containment rule: the claim matching <see cref="Inner"/> must fall within the one matching
/// <see cref="Outer"/>.
/// </summary>
/// <param name="Outer">The target that must contain the other.</param>
/// <param name="Inner">The target that must fall inside.</param>
public sealed record ContainsRule(string Outer, string Inner)
{
    /// <summary>Gets the severity the rule declares.</summary>
    public string Severity { get; init; } = ChronologySeverity.Default;
}

/// <summary>
/// One rule forbidding two assertions from overlapping.
/// </summary>
/// <param name="A">The first target.</param>
/// <param name="B">The second target.</param>
public sealed record OverlapRule(string A, string B)
{
    /// <summary>Gets the severity the rule declares.</summary>
    public string Severity { get; init; } = ChronologySeverity.Default;
}

/// <summary>
/// One deadline rule: the claim matching <see cref="Key"/> is due within <see cref="WithinDays"/> days
/// of the assertion matching <see cref="DueFrom"/>.
/// </summary>
/// <param name="Key">The target that must arrive in time.</param>
/// <param name="DueFrom">The target the clock starts at.</param>
/// <param name="WithinDays">How many days after the start the event is due.</param>
public sealed record DeadlineRule(string Key, string DueFrom, long WithinDays)
{
    /// <summary>Gets the severity the rule declares.</summary>
    public string Severity { get; init; } = ChronologySeverity.Default;
}

/// <summary>
/// One duration rule: the elapsed days between two assertions must stay within bounds.
/// </summary>
/// <param name="Start">The target the interval starts at.</param>
/// <param name="End">The target the interval ends at.</param>
public sealed record DurationRule(string Start, string End)
{
    /// <summary>Gets the lower bound in days, or <see langword="null"/> when unbounded.</summary>
    public long? MinDays { get; init; }

    /// <summary>Gets the upper bound in days, or <see langword="null"/> when unbounded.</summary>
    public long? MaxDays { get; init; }

    /// <summary>Gets the severity the rule declares.</summary>
    public string Severity { get; init; } = ChronologySeverity.Default;
}
