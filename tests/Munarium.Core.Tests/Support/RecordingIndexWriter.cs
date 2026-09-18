namespace Munarium.Core.Tests.Support;

using Munarium.Retrieval;

/// <summary>
/// An <see cref="IIndexWriter"/> that keeps what it was handed, so a test can read the citations and the vectors back.
/// </summary>
/// <param name="indexVersion">The version the writer claims to be writing.</param>
internal sealed class RecordingIndexWriter(string indexVersion = "idx-test") : IIndexWriter
{
    /// <summary>Gets the chunks written, in the order they arrived.</summary>
    public List<IndexedChunk> Chunks { get; } = [];

    /// <inheritdoc />
    public string IndexVersion { get; } = indexVersion;

    /// <inheritdoc />
    public int Count => Chunks.Count;

    /// <inheritdoc />
    public void Index(SourceReference source, string text, ReadOnlySpan<float> embedding) =>
        Chunks.Add(new IndexedChunk(source, text, embedding.ToArray()));
}

/// <summary>One chunk as the writer received it.</summary>
/// <param name="Source">The citation.</param>
/// <param name="Text">The chunk text.</param>
/// <param name="Embedding">The vector the chunk was indexed with.</param>
internal sealed record IndexedChunk(SourceReference Source, string Text, float[] Embedding);
