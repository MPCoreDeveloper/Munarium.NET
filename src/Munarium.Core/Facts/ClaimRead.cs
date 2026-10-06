namespace Munarium.Facts;

/// <summary>
/// One fact read by its claim identity, with whatever now holds its lineage in its place.
/// </summary>
/// <remarks>
/// Both halves are facts the ledger holds rather than a copy of them: what a caller asked for, and - when
/// something later took the lineage - the fact the ledger would serve instead of it, which is what
/// <em>superseded</em> means here. A lineage nothing later wrote to carries <see langword="null"/> for the second,
/// so "this one still holds" and "no such claim" cannot be confused for one another.
/// </remarks>
/// <param name="Claim">The fact that was asked for.</param>
/// <param name="SupersededBy">The fact that holds the lineage now, or <see langword="null"/> when this one does.</param>
public sealed record ClaimRead(SlicedFact Claim, SlicedFact? SupersededBy);
