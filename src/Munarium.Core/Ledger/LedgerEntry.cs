namespace Munarium.Ledger;

/// <summary>
/// An event as it sits in the ledger: where it is, and what it says.
/// </summary>
/// <param name="Sequence">The position within the stream.</param>
/// <param name="GlobalSequence">
/// The position in the ledger as a whole. This is the axis an <c>as_of</c> pin is expressed on,
/// because one pin has to bound facts, anchors and entities together.
/// </param>
/// <param name="Event">The event itself.</param>
public sealed record LedgerEntry(
    SequenceNumber Sequence,
    SequenceNumber GlobalSequence,
    LedgerEvent Event);
