namespace Munarium.Claims;

/// <summary>
/// How a gate finding weighs, ordered from least to most serious.
/// </summary>
/// <remarks>
/// The order is part of the contract, not a convention: a caller that escalates a finding, or that
/// asks whether anything blocked, compares severities rather than enumerating cases.
/// </remarks>
public enum Severity
{
    /// <summary>Recorded for the operator; nothing is refused on it.</summary>
    Info = 0,

    /// <summary>Surfaced as a problem, but the claim is still accepted.</summary>
    Warn = 1,

    /// <summary>The claim is recorded as disputed.</summary>
    Block = 2,
}
