namespace Munarium.Retrieval;

/// <summary>
/// The retrieval seam: hybrid search that always answers with a provenance envelope.
/// </summary>
/// <remarks>
/// Concrete backends (SharpCoreDB's vector index plus full-text search, an external search cluster)
/// live behind this interface, so the kernel never depends on a search engine.
/// </remarks>
public interface IRetrievalBackend
{
    /// <summary>
    /// Searches the active index for a query.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fused chunks together with the envelope that covers them.</returns>
    ValueTask<RetrievalResult> SearchAsync(RetrievalQuery query, CancellationToken cancellationToken = default);
}
