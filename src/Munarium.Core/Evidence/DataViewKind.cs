namespace Munarium.Evidence;

/// <summary>
/// What kind of data view a profile pinned.
/// </summary>
/// <remarks>
/// The kind decides two things and nothing else: which route the view is executed under, and whether the request
/// carries a semantic intent rather than a contract name. A contract is written by hand and has parameters; a metric
/// view and a native data view are asked with names the resolver chose from lists they declare.
/// <para>
/// The route lives here rather than with the client because it is a fact the <em>document</em> implies: a runbook
/// declares which kind a view is, and the kind is what says where the call goes. The client only concatenates.
/// </para>
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
/// What a data view kind implies.
/// </summary>
public static class DataViewKinds
{
    /// <summary>Gets the route segment the kind is executed under.</summary>
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
    public static bool IsSemantic(this DataViewKind kind) => kind is not DataViewKind.Contract;
}
