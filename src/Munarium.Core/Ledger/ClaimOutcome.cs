namespace Munarium.Ledger;

/// <summary>
/// Governance permitted the claim; it is recorded as asserted.
/// </summary>
/// <param name="Head">The stream head after the write.</param>
public sealed record ClaimAsserted(SequenceNumber Head);

/// <summary>
/// Governance blocked the claim; it is recorded as disputed rather than dropped.
/// </summary>
/// <param name="Gate">The gate that blocked it.</param>
/// <param name="Reason">Why it blocked.</param>
/// <param name="Head">The stream head after the write.</param>
public sealed record ClaimRecordedAsDisputed(string Gate, string Reason, SequenceNumber Head);

/// <summary>
/// The write lost every retry: another writer kept moving the head.
/// </summary>
/// <param name="Expected">The head the kernel last attempted with.</param>
/// <param name="Actual">The head the store reported.</param>
public sealed record ClaimContended(SequenceNumber Expected, SequenceNumber Actual);

/// <summary>
/// The outcome of recording a claim, modelled as a C# 15 union.
/// </summary>
/// <remarks>
/// Note that <see cref="ClaimRecordedAsDisputed"/> is a success, not a failure: the command's job
/// was to put the claim and its governance verdict into the ledger, and it did.
/// </remarks>
public readonly union ClaimOutcome(ClaimAsserted, ClaimRecordedAsDisputed, ClaimContended);
