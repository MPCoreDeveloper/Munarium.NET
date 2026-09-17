namespace Munarium.Governance;

using Munarium.Claims;
using Munarium.Ledger;

/// <summary>
/// The batch landed: every claim is in the ledger, with the ones a block named recorded as disputed.
/// </summary>
/// <param name="Claims">
/// The claims as stored. Each carries the position the store assigned it in the version's stream; a
/// snapshot re-reads the same claims with the global position the pin is on.
/// </param>
/// <param name="Findings">
/// Every finding the gates produced, including the warnings that refused nothing. This is the
/// authoritative carrier of the verdict - the ledger records a disputed claim with the rule that refused
/// it, and the detail belongs to the finding.
/// </param>
/// <param name="Head">The version's head after the append.</param>
/// <param name="FindingsSequence">
/// The position the findings were recorded at, or <see langword="null"/> when the write produced none.
/// </param>
public sealed record CandidateRecorded(
    IReadOnlyList<Claim> Claims,
    IReadOnlyList<GateFinding> Findings,
    SequenceNumber Head,
    SequenceNumber? FindingsSequence = null);

/// <summary>
/// The batch did not land: the version's head was not what the gate decision was computed against.
/// </summary>
/// <param name="Expected">The position the caller required, or the last one the writer observed.</param>
/// <param name="Actual">The position the stream actually held.</param>
public sealed record CandidateContended(SequenceNumber Expected, SequenceNumber Actual);

/// <summary>
/// The outcome of recording a candidate: it landed, or its head had moved.
/// </summary>
/// <remarks>
/// A batch is one unit - it lands entirely or not at all - so there is no partial case to model. A
/// candidate that produced only text and no claims is <see cref="CandidateRecorded"/> with no claims: the
/// findings are real, and refusing them because there was nothing to append would make a text-only
/// candidate unjudgeable.
/// </remarks>
public readonly union CandidateOutcome(CandidateRecorded, CandidateContended);
