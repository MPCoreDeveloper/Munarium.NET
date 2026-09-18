namespace Munarium.Evidence;

/// <summary>
/// The lifecycle of an artifact row.
/// </summary>
/// <remarks>
/// Three states, and the middle one is the only readable one. A pending artifact is not evidence yet - a
/// grant was issued and the bytes were never committed, so nothing resolves in that state. A purged artifact
/// keeps its row: retention removed the bytes, and a citation against it must resolve as <em>expired</em>
/// rather than as not found, because "this was sealed and then lawfully removed" and "this never existed" are
/// different answers to give a reader.
/// </remarks>
public enum EvidenceState
{
    /// <summary>A grant was issued; the bytes have not been committed.</summary>
    Pending = 0,

    /// <summary>Bytes committed and both hashes verified. The only readable state.</summary>
    Committed = 1,

    /// <summary>Retention removed the bytes; the row survives so a citation resolves as expired.</summary>
    Purged = 2,
}

/// <summary>
/// The wire names of <see cref="EvidenceState"/>.
/// </summary>
public static class EvidenceStateNames
{
    /// <summary>Returns the name the wire carries for a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The snake_case name.</returns>
    public static string ToWireName(this EvidenceState state) => state switch
    {
        EvidenceState.Pending => "pending",
        EvidenceState.Committed => "committed",
        _ => "purged",
    };

    /// <summary>
    /// Parses a wire name.
    /// </summary>
    /// <param name="value">The name.</param>
    /// <returns>The state, or <see langword="null"/> when the name is not one.</returns>
    public static EvidenceState? ParseState(string? value) => value switch
    {
        "pending" => EvidenceState.Pending,
        "committed" => EvidenceState.Committed,
        "purged" => EvidenceState.Purged,
        _ => null,
    };
}
