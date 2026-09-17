namespace Munarium.Claims;

using System.Text.Json.Nodes;

/// <summary>
/// One thing a gate had to say about a candidate: which rule, how serious, and the detail an operator
/// needs to act on it.
/// </summary>
/// <remarks>
/// A finding is data rather than an exception, for the same reason a blocked claim is written rather
/// than dropped: the verdict has to be reportable. Findings accumulate, because a candidate can break
/// several rules at once and an operator who fixes one of them should not have to re-run to discover
/// the next.
/// <para>
/// <see cref="Detail"/> is JSON so that the same finding can travel to the REST surface, the gRPC
/// surface and an operator's log without a second model per wire. Its load-bearing entry is
/// <c>claim_key</c>: that is what lets the accept path mark the right claim disputed. Note that a
/// finding's record equality therefore compares the detail by reference; a caller that needs to
/// compare details compares them field by field.
/// </para>
/// </remarks>
public sealed record GateFinding
{
    /// <summary>Gets the dotted rule identifier, such as <c>gate.ledger-conflict</c>.</summary>
    public required string RuleId { get; init; }

    /// <summary>Gets how serious the finding is. Only <see cref="Severity.Block"/> refuses a claim.</summary>
    public required Severity Severity { get; init; }

    /// <summary>Gets the finding in the operator's words.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the scope the candidate was judged in, when it was written in one.</summary>
    public string? ScopePath { get; init; }

    /// <summary>Gets the structured detail: the claim key, and the values that disagreed.</summary>
    public JsonObject? Detail { get; init; }

    /// <summary>
    /// Gets the claim key this finding names, or <see langword="null"/> when it names none.
    /// </summary>
    /// <remarks>
    /// A finding that names no claim - a meta-leakage finding about a whole unit of text, say - cannot
    /// dispute one claim, so this is deliberately nullable rather than defaulted to the empty string.
    /// </remarks>
    public string? ClaimKey =>
        Detail?["claim_key"] is JsonValue value && value.TryGetValue(out string? key) ? key : null;
}
