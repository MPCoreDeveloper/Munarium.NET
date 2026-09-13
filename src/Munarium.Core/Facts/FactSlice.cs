namespace Munarium.Facts;

using Munarium.Ledger;

/// <summary>
/// The facts that are current at one <c>as_of</c> pin, and a digest over exactly that state.
/// </summary>
/// <param name="Pin">The pin this slice was taken at.</param>
/// <param name="Facts">The current facts, ordered by lineage.</param>
/// <param name="Digest">
/// The digest of the slice, derived only from the facts above - so the same pin rebuilds the same
/// digest, and a later event cannot change it.
/// </param>
public sealed record FactSlice(SequenceNumber Pin, IReadOnlyList<SlicedFact> Facts, string Digest);
