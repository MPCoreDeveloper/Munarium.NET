namespace Munarium.Facts;

using Munarium.Governance;
using Munarium.Ledger;

/// <summary>
/// A fact that is current at a pin, with the governance verdict it was recorded under.
/// </summary>
/// <param name="Fact">The fact.</param>
/// <param name="Verdict">The verdict the fact was admitted (or refused) under.</param>
/// <param name="GlobalSequence">The global position that made this fact the current one.</param>
public sealed record SlicedFact(FactRecord Fact, ClaimVerdict Verdict, SequenceNumber GlobalSequence);
