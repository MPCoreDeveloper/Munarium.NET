namespace Munarium.Retrieval;

/// <summary>One indexed chunk, as a build writes it and as a restart reads it back.</summary>
/// <remarks>
/// A chunk carries the two things an index is built from - the text a lexical index reads and the embedding a vector index
/// reads - so a deployment that comes back rebuilds neither from the corpus. That matters because the expensive half of a
/// rebuild is not the index: it is reading every bound document and extracting and embedding it again, and a persisted
/// chunk skips all three.
/// <para>
/// The ordinal is part of the record rather than implied by position, because a read has to be able to produce the same
/// order twice: two chunks of one document are two facts about it, and which one came first is not the storage's choice.
/// </para>
/// </remarks>
public sealed record PersistedChunk
{
    /// <summary>Gets where the chunk came from, which is what a citation resolves.</summary>
    public required SourceReference Source { get; init; }

    /// <summary>Gets the chunk's position within its source.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Gets the chunk text.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the chunk's embedding, which is the vector index's input and not its output.</summary>
    public required IReadOnlyList<float> Embedding { get; init; }
}

/// <summary>Where an index version's chunks are kept, so a restart loads them instead of rebuilding them.</summary>
/// <remarks>
/// A version's chunks are written once, when the build that produced them finishes, and read wholesale when a deployment
/// starts and finds a live version it has no instance for. Nothing is updated in place: a correction or a re-chunking is a
/// new version, which is the same rule the catalogue already follows, so a store that only appends can never disagree with
/// an envelope issued yesterday.
/// <para>
/// An implementation has to be able to answer "which rows are this version's" without comparing caller-supplied text,
/// because this port measured that its storage engine compares an identity and nothing else. A version id is text a caller
/// chose, so the row carries a derived key as well - the same reasoning the source rows use for their own identity.
/// </para>
/// </remarks>
public interface IIndexChunkStore
{
    /// <summary>Writes the chunks a build produced.</summary>
    /// <param name="indexVersion">The version they belong to.</param>
    /// <param name="chunks">The chunks, in the order they were indexed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many chunks were written.</returns>
    ValueTask<int> WriteAsync(
        string indexVersion,
        IReadOnlyList<PersistedChunk> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a version's chunks.</summary>
    /// <param name="indexVersion">The version to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chunks in the order they were written, or an empty list when the version has none.</returns>
    ValueTask<IReadOnlyList<PersistedChunk>> ReadAsync(
        string indexVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets a version's chunks, which is what discarding a version does.</summary>
    /// <param name="indexVersion">The version to forget.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the version had chunks to forget.</returns>
    ValueTask<bool> DropAsync(string indexVersion, CancellationToken cancellationToken = default);
}
