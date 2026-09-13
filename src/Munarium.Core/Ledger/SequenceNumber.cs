namespace Munarium.Ledger;

/// <summary>
/// A monotonically increasing position in the ledger. Sequence zero is the position of
/// a stream that holds no events; a single <see cref="SequenceNumber"/> bounds facts,
/// anchors, promises, counters and entities together when used as an <c>as_of</c> pin.
/// </summary>
/// <param name="Value">The underlying sequence value.</param>
public readonly record struct SequenceNumber(long Value) : IComparable<SequenceNumber>
{
    /// <summary>The position of a stream with no events.</summary>
    public static readonly SequenceNumber Zero = new(0);

    /// <summary>
    /// Returns the sequence immediately after this one.
    /// </summary>
    /// <returns>The next sequence number.</returns>
    public SequenceNumber Next() => new(Value + 1);

    /// <inheritdoc />
    public int CompareTo(SequenceNumber other) => Value.CompareTo(other.Value);

    /// <summary>Returns <see langword="true"/> when <paramref name="left"/> precedes <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(SequenceNumber left, SequenceNumber right) => left.Value < right.Value;

    /// <summary>Returns <see langword="true"/> when <paramref name="left"/> precedes or equals <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(SequenceNumber left, SequenceNumber right) => left.Value <= right.Value;

    /// <summary>Returns <see langword="true"/> when <paramref name="left"/> follows <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(SequenceNumber left, SequenceNumber right) => left.Value > right.Value;

    /// <summary>Returns <see langword="true"/> when <paramref name="left"/> follows or equals <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(SequenceNumber left, SequenceNumber right) => left.Value >= right.Value;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
