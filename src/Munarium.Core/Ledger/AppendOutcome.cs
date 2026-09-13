namespace Munarium.Ledger;

/// <summary>
/// The append was written. <see cref="Head"/> is the stream head after the append.
/// </summary>
/// <param name="Head">The new stream head.</param>
public sealed record Appended(SequenceNumber Head);

/// <summary>
/// The append was refused because the stream head had moved since the caller read it.
/// Nothing was written.
/// </summary>
/// <param name="Expected">The head the caller expected.</param>
/// <param name="Actual">The head the store observed.</param>
public sealed record VersionConflict(SequenceNumber Expected, SequenceNumber Actual);

/// <summary>
/// The outcome of a conditional append, modelled as a C# 15 union.
/// </summary>
/// <remarks>
/// A union is the right shape here because the two outcomes are genuinely exclusive and both
/// matter: a caller that forgets to handle <see cref="VersionConflict"/> does not compile.
/// </remarks>
public readonly union AppendOutcome(Appended, VersionConflict);
