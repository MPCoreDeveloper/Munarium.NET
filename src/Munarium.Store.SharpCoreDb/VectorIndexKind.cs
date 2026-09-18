namespace Munarium.Store.SharpCoreDb;

using SharpCoreDB.VectorSearch.Index;

/// <summary>
/// Which vector engine an index version was built by.
/// </summary>
/// <remarks>
/// The choice is an index-version decision rather than a query-time one: two engines over one corpus return
/// different answers, so the engine a version was built by belongs in that version's manifest and in its identity.
/// A query cannot ask to be answered by a different engine than the one that indexed the corpus.
/// </remarks>
public enum VectorIndexKind
{
    /// <summary>Brute-force search. Perfect recall, linear cost - the answer to compare the others against.</summary>
    Exact = 0,

    /// <summary>A Vamana graph with greedy beam search: approximate, and the only approximation is which nodes the beam visits.</summary>
    DiskAnn = 1,
}

/// <summary>
/// The versioned engine references an index manifest records.
/// </summary>
/// <remarks>
/// Versioned for the same reason a chunker is: a change to how the graph is built is a change to the answers, and a
/// manifest that named only "diskann" would let two different builds claim one identity.
/// </remarks>
public static class VectorIndexEngines
{
    /// <summary>The reference an exact index records.</summary>
    public const string Exact = "flat@1";

    /// <summary>The reference a DiskANN index records, taken from the engine itself.</summary>
    public static string DiskAnnFingerprint { get; } =
        string.Concat(DiskAnnIndex.EngineId, "@", DiskAnnIndex.EngineRevision);

    /// <summary>Gets the reference an engine records.</summary>
    /// <param name="kind">The engine.</param>
    /// <returns>The versioned reference.</returns>
    public static string Of(VectorIndexKind kind) =>
        kind is VectorIndexKind.Exact ? Exact : DiskAnnFingerprint;
}
