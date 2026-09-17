namespace Munarium.Chronology;

using System.Text.Json.Nodes;

/// <summary>
/// One contributor to the timeline the chronology rules are evaluated over.
/// </summary>
/// <remarks>
/// The origin is part of the event rather than of the rule, because it decides whether a violation is
/// filed at all: a rule that fires only between two facts of a corpus nobody is writing right now is
/// history, not a finding about the unit under review. Only a rule that involves a candidate-side
/// event is reported - which is what stops a re-run from re-filing every old contradiction.
/// </remarks>
public sealed record ChronoEvent
{
    /// <summary>Gets the subject the event is about.</summary>
    public required string Subject { get; init; }

    /// <summary>Gets the property the event asserts.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the value as asserted - the text the interval was parsed from.</summary>
    public required string Value { get; init; }

    /// <summary>Gets the parsed interval.</summary>
    public required TemporalInterval Interval { get; init; }

    /// <summary>Gets the accepted fact's identity, or <see langword="null"/> for a candidate claim.</summary>
    public string? ClaimId { get; init; }

    /// <summary>Gets whether the event comes from the ledger or from the candidate under review.</summary>
    public required ChronoOrigin Origin { get; init; }

    /// <summary>Gets the claim key the event contributes to.</summary>
    public string ClaimKey => string.Concat(Subject, ".", Key);

    /// <summary>
    /// Projects the event as one link of a violation's chain, tagged with its role in the rule.
    /// </summary>
    /// <param name="role">The role it played: <c>before</c>, <c>after</c>, <c>outer</c>, and so on.</param>
    /// <returns>The event as JSON.</returns>
    public JsonObject ToDetail(string role) => new()
    {
        ["role"] = role,
        ["claim_id"] = ClaimId,
        ["claim"] = ClaimKey,
        ["value"] = Value,
        ["when"] = Interval.ToDetail(),
        ["origin"] = Origin.ToWireName(),
    };
}
