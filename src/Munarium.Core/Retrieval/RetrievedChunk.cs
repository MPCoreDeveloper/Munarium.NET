namespace Munarium.Retrieval;

/// <summary>
/// One retrieved chunk: its provenance, its fused score, and the text the answer may use.
/// </summary>
/// <param name="Source">Where the chunk came from.</param>
/// <param name="Score">The fused ranking score.</param>
/// <param name="Text">The chunk text.</param>
public sealed record RetrievedChunk(SourceReference Source, double Score, string Text);
