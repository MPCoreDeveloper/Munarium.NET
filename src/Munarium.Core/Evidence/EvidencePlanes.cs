namespace Munarium.Evidence;

/// <summary>
/// The planes a layer's pinned sources can come from.
/// </summary>
/// <remarks>
/// A <em>bare</em> source name is a collection, served by the document path. A <em>prefixed</em> one names a plane
/// that has its own provider, and if no provider claims it the layer has to refuse - never fall through to a
/// document search.
/// <para>
/// That fall-through was a live defect in the original (caught by its conformance tier), and it is worth recording
/// why it was so bad: with no semantic plane configured, a layer reading <c>matrix:register</c> quietly became a
/// document search over the session's collections, returned 200, and reported its <em>required</em> layer satisfied
/// with <c>document_hits</c>. The register was never consulted and nothing in the response said so - the answer
/// looked complete.
/// </para>
/// </remarks>
public static class EvidencePlanes
{
    /// <summary>The provider id the document path reports.</summary>
    public const string Documents = "documents";

    /// <summary>The provider id the fact plane reports.</summary>
    public const string Facts = "facts";

    /// <summary>The provider id the semantic data-view plane reports.</summary>
    public const string Matrix = "matrix";

    /// <summary>The prefix of the semantic data-view plane.</summary>
    public const string MatrixPrefix = "matrix:";

    /// <summary>The prefix of the fact plane.</summary>
    public const string FactsPrefix = "facts:";

    /// <summary>The prefix a layer pins a scope prefix with.</summary>
    public const string ScopePrefix = "scope:";

    /// <summary>
    /// Reports whether a source names a plane rather than a document collection.
    /// </summary>
    /// <param name="source">The pinned source.</param>
    /// <returns><see langword="true"/> when the source is plane-qualified.</returns>
    public static bool IsPlaneQualified(string source) =>
        source.StartsWith(MatrixPrefix, StringComparison.Ordinal)
        || source.StartsWith(FactsPrefix, StringComparison.Ordinal);
}
