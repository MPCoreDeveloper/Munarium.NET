namespace Munarium.Evidence;

/// <summary>
/// Who is resolving, and what they may resolve.
/// </summary>
/// <remarks>
/// The rule this carries is stated from the artifact's side as <see cref="AuthorizationClass.DominatedBy(int, IReadOnlyList{string}?, bool)"/>:
/// a reader resolves an artifact only if the reader's level is at least the artifact's and the reader holds every
/// compartment it declares. The principal is passed in rather than read from an ambient, so a caller cannot resolve as
/// somebody else by forgetting to say who it is.
/// </remarks>
public sealed record EvidencePrincipal
{
    /// <summary>Gets the tenant the caller belongs to.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets who is reading, as the audit log records it.</summary>
    public string Uid { get; init; } = "anonymous";

    /// <summary>Gets the level the caller holds.</summary>
    public required int Level { get; init; }

    /// <summary>Gets the compartments the caller holds.</summary>
    public IReadOnlyList<string> Compartments { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the caller holds every compartment.
    /// </summary>
    /// <remarks>
    /// This clears the compartment gate and never the level gate: the level is still compared, so an unrestricted
    /// principal is not an unlimited one.
    /// </remarks>
    public bool AllCompartments { get; init; }

    /// <summary>
    /// The principal a deployment that has no authorization maps every caller to.
    /// </summary>
    /// <remarks>
    /// The original does the same thing with authorization switched off, and states why it is worth being explicit about:
    /// with it disabled every caller is one unrestricted principal that dominates everything, which is a deployment
    /// property rather than a caller's claim. It is a named constructor so that the day authorization lands, every place
    /// that assumed it is one `<c>grep</c>` away.
    /// </remarks>
    /// <param name="tenant">The deployment's tenant.</param>
    /// <returns>The principal.</returns>
    public static EvidencePrincipal ForDeployment(string tenant) => new()
    {
        Tenant = tenant,
        Level = int.MaxValue,
        AllCompartments = true,
    };
}
