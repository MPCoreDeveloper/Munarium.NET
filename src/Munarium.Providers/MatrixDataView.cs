namespace Munarium.Providers;

/// <summary>
/// What kind of data view a profile pinned.
/// </summary>
/// <remarks>
/// The kind decides two things and nothing else: which route the view is executed under, and whether the request
/// carries a semantic intent rather than a contract name. A contract is written by hand and has parameters; a metric
/// view and a native data view are asked with names the resolver chose from the lists they declare.
/// </remarks>
public enum DataViewKind
{
    /// <summary>A pre-declared query contract, with parameters bound by the profile.</summary>
    Contract = 0,

    /// <summary>A metric view, answered from a semantic intent.</summary>
    MetricView = 1,

    /// <summary>A native data view, answered from a semantic intent.</summary>
    DataView = 2,
}

/// <summary>
/// The route segments the view kinds are executed under.
/// </summary>
public static class DataViewKinds
{
    /// <summary>Gets the route segment for a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The segment.</returns>
    public static string Route(this DataViewKind kind) => kind switch
    {
        DataViewKind.MetricView => "metricviews",
        DataViewKind.DataView => "dataviews",
        _ => "contracts",
    };

    /// <summary>Gets a value indicating whether the kind is asked with a semantic intent.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> when the view is semantic.</returns>
    public static bool IsSemantic(this DataViewKind kind) =>
        kind is DataViewKind.MetricView or DataViewKind.DataView;
}

/// <summary>
/// One data view, resolved from a profile at turn time.
/// </summary>
/// <remarks>
/// A layer names the <em>view</em>; Matrix's route takes the <em>contract</em>, and the profile is the only place that
/// mapping exists - which is what keeps a turn from naming a contract nobody reviewed.
/// </remarks>
public sealed record BoundDataView
{
    /// <summary>Gets the <c>name@version</c> of the contract.</summary>
    public required string Contract { get; init; }

    /// <summary>Gets the kind of view, which decides the route and whether the intent is semantic.</summary>
    public required DataViewKind Kind { get; init; }

    /// <summary>
    /// Gets the contract's parameters, as a JSON object, bound by the profile.
    /// </summary>
    /// <remarks>
    /// Held as the JSON text the profile wrote rather than as a parsed model, because the kernel has no parameter
    /// vocabulary to validate it against: the contract's own schema is what says whether a parameter is bound, and
    /// Matrix is what tells a caller it is not. An empty object means the parameters were all bound by the contract.
    /// </remarks>
    public string ParametersJson { get; init; } = "{}";

    /// <summary>Gets the access level the view was declared for.</summary>
    public required int AccessLevel { get; init; }

    /// <summary>
    /// Gets the compartments the view was declared for.
    /// </summary>
    /// <remarks>
    /// Empty means the view is not compartmented, so every compartment the session holds is sent. A view that names
    /// compartments gets the intersection, which is the only way a session can be held to what the view was declared
    /// for.
    /// </remarks>
    public IReadOnlyList<string> Compartments { get; init; } = [];
}

/// <summary>
/// The session's own authorization, sent verbatim in the intent.
/// </summary>
/// <remarks>
/// Sent so Matrix can re-check it against the token's tenant and refuse a mismatch, which is what stops a caller
/// sealing evidence into someone else's tenant by editing a field: the plane decides, not the caller.
/// </remarks>
public sealed record SessionAuthorization
{
    /// <summary>Gets the tenant the session belongs to.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the user the session is running as.</summary>
    public required string Uid { get; init; }

    /// <summary>Gets the access level the session holds.</summary>
    public required int AccessLevel { get; init; }

    /// <summary>Gets the compartments the session holds.</summary>
    public IReadOnlyList<string> Compartments { get; init; } = [];

    /// <summary>Gets the session's identity, so the plane can attribute the read.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the research profile the turn is running, as <c>name@version</c>.</summary>
    public required string RunbookRef { get; init; }
}
