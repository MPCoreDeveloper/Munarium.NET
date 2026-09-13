namespace Munarium.Ledger;

/// <summary>
/// Identifies an append-only event stream in the fact ledger. A stream is the unit
/// of ordering: supersession chains and the <c>as_of</c> pin both key off it.
/// </summary>
/// <param name="Value">The caller-supplied stream name, unique within a tenant.</param>
public readonly record struct StreamId(string Value)
{
    /// <summary>
    /// Creates a stream identifier, rejecting null, empty or whitespace values.
    /// </summary>
    /// <param name="value">The stream name.</param>
    /// <returns>The stream identifier.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="value"/> is <see langword="null"/>, empty or whitespace.
    /// </exception>
    public static StreamId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new StreamId(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
