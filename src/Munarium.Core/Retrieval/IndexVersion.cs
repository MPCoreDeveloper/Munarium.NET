namespace Munarium.Retrieval;

using System.Globalization;
using Munarium.Ledger;

/// <summary>
/// One source bound into an index version: its identity, and the bytes it held when it was indexed.
/// </summary>
/// <remarks>
/// Identity is paired with the hash on purpose. Two sources that happen to share bytes are still two sources, so an
/// index over them is a different index from one holding either alone - and re-putting one path with new bytes is a
/// different index too, which is the case that matters: the answer must not keep citing bytes the path no longer
/// holds.
/// </remarks>
/// <param name="SourceId">The source's stable identity.</param>
/// <param name="ContentHash">The hash of the bytes it held at index time.</param>
public sealed record IndexedSource(string SourceId, string ContentHash);

/// <summary>
/// Which embedder produced an index version's vectors.
/// </summary>
/// <param name="Provider">The provider, such as <c>local</c>.</param>
/// <param name="Model">The model's name.</param>
/// <param name="Dimensions">The vector width.</param>
public sealed record EmbedderRef(string Provider, string Model, int Dimensions)
{
    /// <summary>Gets the embedder's fingerprint, which is what the identity hashes.</summary>
    public string Fingerprint =>
        string.Create(CultureInfo.InvariantCulture, $"{Provider}/{Model}@{Dimensions}");
}

/// <summary>
/// What an index version was built from, recorded so a version can be explained without being re-derived.
/// </summary>
/// <remarks>
/// Every field here is identity material: a change to any of them changes the text or the vectors a query would
/// match, so it has to produce a new version rather than silently serving the old chunks. That is why the extractor
/// version is in the list - improving how a DOCX is turned into text changes the text for identical bytes.
/// </remarks>
public sealed record IndexManifest
{
    /// <summary>Gets the collection the version indexes.</summary>
    public required string CollectionId { get; init; }

    /// <summary>Gets the collection's name, so an operator reading a manifest needs no second lookup.</summary>
    public required string CollectionName { get; init; }

    /// <summary>Gets the contract shape the corpus was mapped through.</summary>
    public required string ShapeRef { get; init; }

    /// <summary>Gets the chunker's version.</summary>
    public required string Chunker { get; init; }

    /// <summary>Gets the extractors' version.</summary>
    public required string Extractors { get; init; }

    /// <summary>Gets the embedder that produced the vectors.</summary>
    public required EmbedderRef Embedder { get; init; }

    /// <summary>
    /// Gets the hash of every source the version indexed.
    /// </summary>
    /// <remarks>
    /// The manifest records which <em>bytes</em> were indexed, which is what lets an envelope citing bytes outside
    /// this set be refused: an answer cannot have come from a version that never held them.
    /// </remarks>
    public required IReadOnlyList<string> SourceContentHashes { get; init; }

    /// <summary>Gets the character ceiling a chunk was cut at.</summary>
    public required int MaxChars { get; init; }
}

/// <summary>
/// An index version: an immutable snapshot of one collection's corpus as one shape sees it.
/// </summary>
/// <remarks>
/// The identity is content-addressed - it hashes everything that determines what a query would match - so
/// rebuilding the same corpus under the same policies <em>is</em> the same version, and any change that alters the
/// text or the vectors is a new one. That is what makes a build idempotent and a cutover meaningful: the version an
/// answer cites either still exists or never did.
/// <para>
/// A version is never mutated by a rebuild. A correction, a re-chunk or a new embedder produces another version and
/// a separate cutover, which is what lets an envelope issued yesterday still be verified today - it names a version
/// that is still there, merely no longer active.
/// </para>
/// </remarks>
public sealed record IndexVersion
{
    /// <summary>
    /// Gets the identity, which only <see cref="IndexVersionIds"/> mints.
    /// </summary>
    /// <remarks>
    /// A caller never invents this: an identity chosen rather than derived would let two different corpora share a
    /// name, and an envelope's promise that it can be resolved later would then be worth nothing.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>Gets the tenant the version belongs to.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the collection the version indexes.</summary>
    public required string CollectionId { get; init; }

    /// <summary>Gets the contract shape the version answers for.</summary>
    public required string ShapeRef { get; init; }

    /// <summary>Gets what the version was built from.</summary>
    public required IndexManifest Manifest { get; init; }

    /// <summary>
    /// Gets the ledger position the version reflects.
    /// </summary>
    /// <remarks>
    /// This is where an answer's provenance ends: chunk, envelope, index version, and here - the ledger position
    /// the index was built against. Without it a citation proves which bytes were read and not which state of the
    /// world was believed.
    /// </remarks>
    public required SequenceNumber Watermark { get; init; }

    /// <summary>Gets a value indicating whether the version is the collection's live one.</summary>
    public bool Active { get; init; }

    /// <summary>Gets when the version was first made live.</summary>
    public DateTimeOffset? ActivatedAt { get; init; }

    /// <summary>
    /// Gets when the version stopped being live.
    /// </summary>
    /// <remarks>
    /// A superseded version stays resolvable and stays readable: cutting over changes which version answers a new
    /// query, never whether an old answer can still be explained.
    /// </remarks>
    public DateTimeOffset? DeactivatedAt { get; init; }

    /// <summary>Gets a value indicating whether the version was once live and is not now.</summary>
    public bool Superseded => !Active && DeactivatedAt is not null;
}
