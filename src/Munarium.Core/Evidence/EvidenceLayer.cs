namespace Munarium.Evidence;

/// <summary>
/// What weight a layer's evidence carries in an answer.
/// </summary>
/// <remarks>
/// The roles are ordered, and the order is the meaning: supporting corroborates and never decides alone, primary
/// is the ordinary answer-bearing role, and controlling decides a conflict.
/// <para>
/// The original mirrors its shapes crate's authority role here rather than importing it, because that crate
/// depends on a JSON-schema library and the kernel is pure. The port has no authority role yet, so this is its
/// own vocabulary - and when the shape ceiling lands, the two have to be kept in step by tests rather than by a
/// shared type, exactly as the original does.
/// </para>
/// </remarks>
public enum AnswerRole
{
    /// <summary>Corroborates; never decides alone.</summary>
    Supporting = 0,

    /// <summary>The ordinary answer-bearing role.</summary>
    Primary = 1,

    /// <summary>Decides a conflict.</summary>
    Controlling = 2,
}

/// <summary>
/// Whether a layer's evidence is required for the answer to stand.
/// </summary>
public enum LayerRequirement
{
    /// <summary>The turn refuses if this layer cannot produce evidence.</summary>
    Required = 0,

    /// <summary>Contributes when available; silence is fine.</summary>
    Optional = 1,

    /// <summary>Consulted only when an earlier layer produced nothing.</summary>
    Fallback = 2,
}

/// <summary>
/// The witness names of the hierarchy's two closed vocabularies.
/// </summary>
public static class HierarchyNames
{
    /// <summary>Returns the name the wire carries for a role.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The snake_case name.</returns>
    public static string ToWireName(this AnswerRole role) => role switch
    {
        AnswerRole.Supporting => "supporting",
        AnswerRole.Primary => "primary",
        _ => "controlling",
    };

    /// <summary>Returns the name the wire carries for a requirement.</summary>
    /// <param name="requirement">The requirement.</param>
    /// <returns>The snake_case name.</returns>
    public static string ToWireName(this LayerRequirement requirement) => requirement switch
    {
        LayerRequirement.Required => "required",
        LayerRequirement.Optional => "optional",
        _ => "fallback",
    };

    /// <summary>Parses a role name.</summary>
    /// <param name="value">The name.</param>
    /// <returns>The role, or <see langword="null"/> when the name is not one.</returns>
    public static AnswerRole? ParseRole(string? value) => value switch
    {
        "supporting" => AnswerRole.Supporting,
        "primary" => AnswerRole.Primary,
        "controlling" => AnswerRole.Controlling,
        _ => null,
    };

    /// <summary>Parses a requirement name.</summary>
    /// <param name="value">The name.</param>
    /// <returns>The requirement, or <see langword="null"/> when the name is not one.</returns>
    public static LayerRequirement? ParseRequirement(string? value) => value switch
    {
        "required" => LayerRequirement.Required,
        "optional" => LayerRequirement.Optional,
        "fallback" => LayerRequirement.Fallback,
        _ => null,
    };
}

/// <summary>
/// One layer of a research profile, resolved and ready to execute.
/// </summary>
public sealed record EvidenceLayer
{
    /// <summary>Gets the stable name, used in the hierarchy decision and in progress reporting.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the pinned sources this layer reads.
    /// </summary>
    /// <remarks>
    /// Pinned when the profile is applied, never resolved during a turn, so a turn cannot silently widen its own
    /// reach - the one thing a research profile exists to prevent.
    /// </remarks>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Gets whether the layer's evidence is required.</summary>
    public required LayerRequirement Requirement { get; init; }

    /// <summary>Gets the weight the layer's evidence carries.</summary>
    public required AnswerRole Role { get; init; }

    /// <summary>Gets the character budget for this layer's contribution to the composed context.</summary>
    public int? ContextCharBudget { get; init; }

    /// <summary>
    /// Gets a value indicating whether a complete result has to survive composition intact or not be used.
    /// </summary>
    /// <remarks>
    /// Half a table is not a smaller true answer, it is a false one: a reader who sees six of twelve rows cannot
    /// tell that the answer is partial.
    /// </remarks>
    public bool PreserveCompleteResult { get; init; }

    /// <summary>Gets how long the layer may take before it is abandoned.</summary>
    public long? DeadlineMilliseconds { get; init; }
}
