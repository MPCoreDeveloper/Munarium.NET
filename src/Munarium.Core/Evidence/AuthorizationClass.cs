namespace Munarium.Evidence;

/// <summary>
/// The equivalence class an artifact belongs to, and the rule that decides who may read it.
/// </summary>
/// <remarks>
/// This is the simple-security property stated from the artifact's side: a reader at a level holding some
/// compartments may resolve the artifact only if the reader's level dominates <em>and</em> every compartment on
/// the artifact is held. The rule is the same one the collection access path applies, on purpose - evidence is
/// not a weaker class of data than the documents beside it, and two domination rules in one server is one too
/// many.
/// </remarks>
public sealed record AuthorizationClass
{
    /// <summary>Gets the class's name, when the deployment names one.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the level required to read the artifact.</summary>
    public required int AccessLevel { get; init; }

    /// <summary>Gets the compartments the artifact is in.</summary>
    public IReadOnlyList<string> Compartments { get; init; } = [];

    /// <summary>
    /// Reports whether a reader dominates this class.
    /// </summary>
    /// <param name="level">The reader's level.</param>
    /// <param name="compartments">The compartments the reader holds.</param>
    /// <param name="allCompartments">
    /// Whether the reader is unrestricted in compartments, which is what a static read-write deployment is.
    /// </param>
    /// <returns><see langword="true"/> when the reader may resolve the artifact.</returns>
    /// <remarks>
    /// An unrestricted reader clears the compartment gate and <em>never</em> the level gate: the level is still
    /// compared, so an explicitly low-level principal cannot read above itself just by being unrestricted.
    /// </remarks>
    public bool DominatedBy(int level, IReadOnlyList<string>? compartments, bool allCompartments)
    {
        var holds = allCompartments ||
            Compartments.All(needed => compartments is not null && compartments.Contains(needed, StringComparer.Ordinal));

        return level >= AccessLevel && holds;
    }
}

/// <summary>
/// When an artifact's bytes have to go, unless something forbids it.
/// </summary>
public sealed record Retention
{
    /// <summary>Gets when the artifact expires.</summary>
    public string? ExpiresAt { get; init; }

    /// <summary>Gets a value indicating whether a legal hold stops the purge.</summary>
    public bool LegalHold { get; init; }

    /// <summary>
    /// Gets when the purge job removed the bytes.
    /// </summary>
    /// <remarks>
    /// A purged artifact keeps its row so a citation resolves as <em>expired</em> rather than as not found.
    /// </remarks>
    public string? PurgedAt { get; init; }
}
