namespace Munarium.Sessions;

/// <summary>
/// The lifecycle of a session.
/// </summary>
/// <remarks>
/// Three states, and closing is one-way: a session that was closed and reopened would be a conversation whose earlier
/// turns answered under a different clearance, which is exactly what the snapshot exists to prevent.
/// </remarks>
public enum SessionState
{
    /// <summary>Accepting turns.</summary>
    Open = 0,

    /// <summary>Closed by the caller; turns are refused.</summary>
    Closed = 1,

    /// <summary>Expired by the deployment's own policy; turns are refused.</summary>
    Expired = 2,
}

/// <summary>
/// The wire names of <see cref="SessionState"/>.
/// </summary>
public static class SessionStateNames
{
    /// <summary>Returns the name the wire carries for a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The lowercase name.</returns>
    public static string ToWireName(this SessionState state) => state switch
    {
        SessionState.Open => "open",
        SessionState.Closed => "closed",
        _ => "expired",
    };

    /// <summary>Parses a wire name.</summary>
    /// <param name="value">The name.</param>
    /// <returns>The state, or <see langword="null"/> when the name is not one.</returns>
    public static SessionState? ParseState(string? value) => value switch
    {
        "open" => SessionState.Open,
        "closed" => SessionState.Closed,
        "expired" => SessionState.Expired,
        _ => null,
    };
}
