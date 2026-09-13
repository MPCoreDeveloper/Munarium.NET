namespace Munarium.Retrieval;

/// <summary>
/// A retrieval answer.
/// </summary>
/// <remarks>
/// The envelope is not optional and not nullable: an answer without provenance cannot be
/// constructed, so "every retrieval answer carries a provenance envelope" is a property of the type
/// rather than a rule someone has to remember.
/// </remarks>
/// <param name="Chunks">The retrieved chunks, in fused rank order.</param>
/// <param name="Envelope">The provenance envelope covering exactly those chunks.</param>
public sealed record RetrievalResult(IReadOnlyList<RetrievedChunk> Chunks, ProvenanceEnvelope Envelope);
