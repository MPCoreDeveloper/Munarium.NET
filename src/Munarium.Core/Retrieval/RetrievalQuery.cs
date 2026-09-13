namespace Munarium.Retrieval;

/// <summary>
/// A retrieval request: the question text, an optional pre-computed query embedding, and how many
/// chunks the answer may use.
/// </summary>
public sealed record RetrievalQuery
{
    /// <summary>Gets the question text, which drives the lexical leg.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the query embedding, when the caller already has one. Embeddings come from the provider
    /// layer, so the retrieval seam never talks to a model itself.
    /// </summary>
    public ReadOnlyMemory<float> Embedding { get; init; }

    /// <summary>Gets how many fused chunks the answer may carry.</summary>
    public int TopK { get; init; } = 10;
}
