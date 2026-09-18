namespace Munarium.Access;

/// <summary>
/// Who is asking, and what they are allowed to see.
/// </summary>
/// <remarks>
/// The kernel does not authenticate anybody - a caller establishes identity outside it - but it does have to be able to
/// say whether a clearance dominates a compartment, because a retrieval plane whose filtering lived in its transport
/// would filter differently on each transport. This type is the original's <c>AccessCtx</c> reduced to the part the data
/// plane actually reads.
/// <para>
/// A session snapshots one of these at creation and uses the snapshot for every turn, so a token that changes mid
/// conversation or a runbook that is upgraded mid conversation never changes what an ongoing conversation can see.
/// That is the point of the snapshot, and it is why the session carries a value of this type rather than a caller's
/// live claims.
/// </para>
/// </remarks>
/// <param name="Level">The capability's access level; higher sees more.</param>
/// <param name="Compartments">The need-to-know tags the capability carries.</param>
/// <param name="AllCompartments">Whether the capability clears every compartment - what an unrestricted context holds.</param>
/// <param name="Runbooks">
/// The runbook names this capability may use, or <see langword="null"/> for any name. Names rather than <c>name@version</c>
/// refs on purpose: one capability spans a runbook's versions.
/// </param>
public sealed record AccessContext(
    int Level,
    IReadOnlyList<string> Compartments,
    bool AllCompartments = false,
    IReadOnlyList<string>? Runbooks = null)
{
    /// <summary>
    /// Gets a context that is allowed everything.
    /// </summary>
    /// <remarks>
    /// What a control-plane credential maps to. It is a value rather than a special case so every gate stays one
    /// predicate: a filter that had to check "is this the admin path" before asking would be a filter with two ways to
    /// be wrong.
    /// </remarks>
    public static AccessContext Unrestricted { get; } = new(int.MaxValue, [], AllCompartments: true);

    /// <summary>
    /// Answers whether this context dominates the level and compartments of one collection.
    /// </summary>
    /// <remarks>
    /// Every compartment the collection requires has to be carried - the original's <c>all</c>, not its <c>any</c> - and
    /// a capability that clears all compartments clears the gate outright. Level and compartment are both inequalities
    /// the caller has to satisfy; neither can substitute for the other.
    /// </remarks>
    /// <param name="level">The collection's access level.</param>
    /// <param name="compartments">The collection's required compartments.</param>
    /// <returns>Whether the collection may be read.</returns>
    public bool Permits(int level, IReadOnlyList<string> compartments)
    {
        ArgumentNullException.ThrowIfNull(compartments);

        return Level >= level
            && (AllCompartments
                || compartments.All(required => Compartments.Contains(required, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Answers whether this context may use a runbook.
    /// </summary>
    /// <param name="name">The runbook's name, without its version.</param>
    /// <returns>Whether the name is permitted, which it is when no allowlist was declared.</returns>
    public bool PermitsRunbook(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Runbooks is null || Runbooks.Contains(name, StringComparer.Ordinal);
    }
}
