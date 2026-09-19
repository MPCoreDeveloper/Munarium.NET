namespace Munarium.Retrieval;

using Munarium.Ledger;

/// <summary>
/// One built index: the version it holds, the writer that filled it, and the reader that answers from it.
/// </summary>
/// <remarks>
/// The three travel together because they are one instance of one engine's index. A version is not a row someone
/// wrote - it is a set of chunks that exists somewhere, and this is where.
/// </remarks>
/// <param name="Version">The index version this instance holds.</param>
/// <param name="Writer">The writer that feeds it.</param>
/// <param name="Reader">The reader that answers from it.</param>
public sealed record IndexInstance(string Version, IIndexWriter Writer, IRetrievalBackend Reader);

/// <summary>
/// The index instances a deployment has: one live, and as many built-but-not-serving as an operator has built.
/// </summary>
/// <remarks>
/// The seam exists so that "build without serving" is a real state rather than a promise: a build creates an instance
/// and keeps it, and serving it later is a cutover - which is what lets a corpus be rebuilt and inspected while the
/// previous version keeps answering questions. An instance that was never built here cannot be served, so a cutover
/// cannot name a version that does not exist in this process.
/// <para>
/// <see cref="Engine"/> is on the host and not on the build because it is identity material: the manifest records the
/// engine that actually built the vectors, and a caller that could name it could record an engine that did not.
/// </para>
/// </remarks>
public interface IIndexHost
{
    /// <summary>Gets the versioned engine reference this host builds with, which a manifest records.</summary>
    string Engine { get; }

    /// <summary>Gets the version that answers now.</summary>
    string ServingVersion { get; }

    /// <summary>
    /// Gets the writer that feeds the serving version, which is where an ingest lands.
    /// </summary>
    /// <remarks>
    /// Read at the moment of use rather than held by a caller: a caller that cached a writer would keep feeding a
    /// version that stopped serving, and its chunks would answer nothing.
    /// </remarks>
    IIndexWriter ServingWriter { get; }

    /// <summary>Gets the reader the serving version answers from.</summary>
    IRetrievalBackend ServingReader { get; }

    /// <summary>
    /// Gets the reader a version built here answers from, without making it the one that serves.
    /// </summary>
    /// <remarks>
    /// Serving is a cutover, and a deployment has one of those; reading is not. A turn searches each collection's own
    /// live version - which is what makes a collection a corpus rather than a label on one - so it needs a reader for a
    /// version the deployment did not cut over to. A version this process never built has no reader to hand back, which
    /// is the same answer a cutover to it gets, and for the same reason: its chunks are not here.
    /// </remarks>
    /// <param name="indexVersion">The version to read.</param>
    /// <returns>The reader, or <see langword="null"/> when its chunks are not in this process.</returns>
    IRetrievalBackend? ReaderFor(string indexVersion);

    /// <summary>
    /// Creates an empty instance for a version, and keeps it so it can be served later.
    /// </summary>
    /// <param name="indexVersion">The version to build into.</param>
    /// <param name="watermark">The ledger position the build reflects.</param>
    /// <returns>The instance, empty.</returns>
    IndexInstance Build(string indexVersion, SequenceNumber watermark);

    /// <summary>
    /// Drops a version that was being built here.
    /// </summary>
    /// <remarks>
    /// A build that refuses must not leave an instance behind, or a half-built corpus could be activated by name and
    /// answer with the documents the build got to before it stopped. Dropping a version that is currently serving is
    /// not a cutover - it is refused, because that would leave the deployment answering from nothing.
    /// </remarks>
    /// <param name="indexVersion">The version to drop.</param>
    /// <returns><see langword="false"/> when that version is not built here or is the one serving.</returns>
    bool Discard(string indexVersion);

    /// <summary>
    /// Serves a built version from now on.
    /// </summary>
    /// <param name="indexVersion">The version to serve.</param>
    /// <returns><see langword="false"/> when that version was never built here, in which case nothing changed.</returns>
    bool Serve(string indexVersion);
}
