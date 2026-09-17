namespace Munarium.Chronology;

using System.Text.Json.Nodes;

/// <summary>
/// Which rule family a chronology violation came from.
/// </summary>
public enum ChronoRuleKind
{
    /// <summary>An ordering rule: a claim is definitely earlier than one it must follow.</summary>
    Order = 0,

    /// <summary>A containment rule: a claim is definitely outside the interval it must fall within.</summary>
    Contains = 1,

    /// <summary>A rule against two definite, day-precise intervals overlapping.</summary>
    Overlap = 2,

    /// <summary>A deadline rule: an event is definitely late, or absent past its deadline.</summary>
    Deadline = 3,

    /// <summary>A duration rule: the elapsed time is definitely outside its bounds.</summary>
    Duration = 4,
}

/// <summary>
/// The wire names of <see cref="ChronoRuleKind"/>, which travel with a finding's detail.
/// </summary>
public static class ChronoRuleKindExtensions
{
    /// <summary>
    /// Returns the name the wire carries for a rule kind.
    /// </summary>
    /// <param name="kind">The rule kind.</param>
    /// <returns>The snake_case name.</returns>
    public static string ToWireName(this ChronoRuleKind kind) => kind switch
    {
        ChronoRuleKind.Order => "order",
        ChronoRuleKind.Contains => "contains",
        ChronoRuleKind.Overlap => "overlap",
        ChronoRuleKind.Deadline => "deadline",
        _ => "duration",
    };
}

/// <summary>
/// One rule that fired, with everything an operator needs to see why.
/// </summary>
/// <remarks>
/// The chain is the complete set of events the rule read - both the ledger's and the candidate's - so a
/// reader can check the reasoning rather than trust the verdict. <see cref="CandidateClaims"/> is what
/// makes the violation attributable: only the claims the candidate itself contributed can be disputed,
/// and a finding that blamed the ledger's history would dispute a claim nobody is writing.
/// </remarks>
public sealed record ChronoViolation
{
    /// <summary>Gets the rule family that fired.</summary>
    public required ChronoRuleKind Kind { get; init; }

    /// <summary>Gets the severity as the rule declared it. Only <c>block</c> refuses the claim.</summary>
    public required string Severity { get; init; }

    /// <summary>Gets the violation in the operator's words.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the rule that fired, as it was declared.</summary>
    public required JsonObject Rule { get; init; }

    /// <summary>Gets the complete role-tagged event chain the rule read.</summary>
    public required IReadOnlyList<JsonObject> Chain { get; init; }

    /// <summary>Gets the extra numbers the message refers to, such as the deadline or the gap.</summary>
    public required JsonObject Extras { get; init; }

    /// <summary>Gets the claim keys the candidate contributed to this violation.</summary>
    public required IReadOnlyList<string> CandidateClaims { get; init; }
}
