namespace Munarium.Runbooks;

/// <summary>
/// Where an applied runbook stands.
/// </summary>
/// <remarks>
/// Removal is a soft transition in three steps, and the YAML is kept at every one of them: a runbook that was removed is
/// inaccessible rather than gone, because the conversations that ran under it still cite it and an audit that could not
/// read the document those turns ran on would be reading a hole.
/// </remarks>
public enum RunbookStatus
{
    /// <summary>Applied and usable.</summary>
    Active = 0,

    /// <summary>A removal was asked for and not yet confirmed; still usable until it is.</summary>
    RemoveRequested = 1,

    /// <summary>Removed: hidden from listing and refused by sessions.</summary>
    Removed = 2,
}

/// <summary>
/// The wire names of <see cref="RunbookStatus"/>.
/// </summary>
public static class RunbookStatusNames
{
    /// <summary>Returns the name the wire carries for a status.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The lowercase name.</returns>
    public static string ToWireName(this RunbookStatus status) => status switch
    {
        RunbookStatus.Active => "active",
        RunbookStatus.RemoveRequested => "remove_requested",
        _ => "removed",
    };

    /// <summary>Parses a wire name.</summary>
    /// <param name="value">The name.</param>
    /// <returns>The status, or <see langword="null"/> when the name is not one.</returns>
    public static RunbookStatus? ParseStatus(string? value) => value switch
    {
        "active" => RunbookStatus.Active,
        "remove_requested" => RunbookStatus.RemoveRequested,
        "removed" => RunbookStatus.Removed,
        _ => null,
    };
}
