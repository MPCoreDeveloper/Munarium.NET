namespace Munarium.Retrieval;

/// <summary>
/// The write side of an index: chunks go in, and nothing is served until the catalogue says so.
/// </summary>
/// <remarks>
/// Separate from <see cref="IRetrievalBackend"/> on purpose. Reading an index version and writing one are different
/// jobs with different lifetimes: a reader has to keep answering from the version it was handed while a build is in
/// progress, which is only possible if the two are separate seams. A backend may well implement both - SharpCoreDB's
/// does - but nothing that only searches depends on the ability to write.
/// </remarks>
public interface IIndexWriter
{
    /// <summary>Gets the version chunks written here belong to.</summary>
    /// <remarks>
    /// The writer knows its own version because a chunk's citation carries it: an answer written from this index has
    /// to name the version, and the envelope is produced by whoever holds both.
    /// </remarks>
    string IndexVersion { get; }

    /// <summary>Gets how many chunks have been written here.</summary>
    int Count { get; }

    /// <summary>
    /// Adds one chunk.
    /// </summary>
    /// <remarks>
    /// Append-only: a correction or a re-chunk is a new version rather than a mutation, which is what lets an
    /// envelope issued yesterday still be verified today.
    /// </remarks>
    /// <param name="source">Where the chunk came from.</param>
    /// <param name="text">The chunk text.</param>
    /// <param name="embedding">The chunk's embedding, which comes from the provider layer.</param>
    void Index(SourceReference source, string text, ReadOnlySpan<float> embedding);
}
