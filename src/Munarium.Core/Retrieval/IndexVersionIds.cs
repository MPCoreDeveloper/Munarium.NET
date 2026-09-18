namespace Munarium.Retrieval;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Mints an index version's identity from everything that determines what a query would match.
/// </summary>
/// <remarks>
/// The identity is a hash rather than a counter because it has to answer two questions at once: whether a build is
/// a rebuild of an existing version (the same material hashes to the same id, so the build is idempotent), and
/// whether two versions can collide (a hash over the material means they collide only if the material does).
/// <para>
/// Segments are joined with a unit separator rather than a printable one, and the sources are sorted and deduped
/// before hashing. Both close the same class of bug: an identity built by joining text with a character that can
/// also appear in the text can be reached two ways - a source id holding the separator would make two different
/// corpora hash to one identity, and a caller's ordering would make one corpus hash to two. The evidence plane
/// learned this the hard way for its domain key; the retrieval identity fixes it the same way.
/// </para>
/// </remarks>
public static class IndexVersionIds
{
    /// <summary>The prefix every index version identity carries.</summary>
    public const string Prefix = "idx-";

    private const char Unit = '\u001f';

    /// <summary>
    /// Mints an identity.
    /// </summary>
    /// <param name="collectionId">The collection the version indexes.</param>
    /// <param name="shapeRef">The contract shape the corpus was mapped through.</param>
    /// <param name="chunker">The chunker's version.</param>
    /// <param name="extractors">The extractors' version.</param>
    /// <param name="embedder">The embedder that produced the vectors.</param>
    /// <param name="sources">The sources bound into the version, in any order.</param>
    /// <returns>The identity, as <c>idx-</c> plus sixteen lowercase hex characters.</returns>
    /// <exception cref="ArgumentException">Thrown when identity material is missing.</exception>
    public static string Of(
        string collectionId,
        string shapeRef,
        string chunker,
        string extractors,
        EmbedderRef embedder,
        IReadOnlyList<IndexedSource> sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(shapeRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunker);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractors);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(sources);

        var bindings = sources
            .Select(source => string.Concat(source.SourceId, Unit, source.ContentHash))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        var material = string.Join(
            Unit,
            collectionId,
            shapeRef,
            chunker,
            embedder.Fingerprint,
            extractors,
            string.Join(Unit, bindings));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return string.Concat(Prefix, Convert.ToHexStringLower(digest)[..16]);
    }
}
