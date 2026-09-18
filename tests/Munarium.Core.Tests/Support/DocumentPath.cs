namespace Munarium.Core.Tests.Support;

using Munarium.Evidence;
using Munarium.Ledger;
using Munarium.Retrieval;

/// <summary>
/// A document path that answers with canned chunks, and counts the layers it was asked about.
/// </summary>
/// <remarks>
/// Shared rather than duplicated, for the same reason the plan fixture is: a suite that runs layers and a suite that
/// turns their evidence into an answer have to mean the same thing by "a document path".
/// </remarks>
public sealed class DocumentPath(params string[] chunkIds)
{
    /// <summary>Gets how many times the path was asked to run.</summary>
    public int Calls { get; private set; }

    /// <summary>Gets the layers the path was asked about, in the order it was asked.</summary>
    public List<string> LayerNames { get; } = [];

    /// <summary>
    /// Runs the path for one layer.
    /// </summary>
    /// <param name="layer">The layer asking.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One chunk per configured id.</returns>
    public ValueTask<RetrievalResult> RunAsync(EvidenceLayer layer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layer);
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LayerNames.Add(layer.Name);

        var chunks = chunkIds.Select(Chunk).ToList();

        return ValueTask.FromResult(new RetrievalResult(
            chunks,
            new ProvenanceEnvelope("index@1", SequenceNumber.Zero, [.. chunks.Select(chunk => chunk.Source)])));
    }

    private static RetrievedChunk Chunk(string chunkId) => new(
        new SourceReference(chunkId, "source-1", "docs/policy.pdf", "sha256:abc", ChunkOrdinal: 0),
        Score: 0,
        $"text of {chunkId}");
}
