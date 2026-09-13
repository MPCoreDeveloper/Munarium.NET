namespace Munarium.Retrieval;

/// <summary>
/// Where one retrieved chunk came from.
/// </summary>
/// <param name="ChunkId">The chunk's stable identity, unique within an index version.</param>
/// <param name="SourceId">The document's identity.</param>
/// <param name="SourcePath">
/// The logical path the document was stored under. An answer has to be able to say <em>which
/// document</em> answered - a bare content hash never did.
/// </param>
/// <param name="ContentHash">The document's content hash, verified at ingest.</param>
/// <param name="ChunkOrdinal">The chunk's position within its document.</param>
public sealed record SourceReference(
    string ChunkId,
    string SourceId,
    string SourcePath,
    string ContentHash,
    int ChunkOrdinal);
