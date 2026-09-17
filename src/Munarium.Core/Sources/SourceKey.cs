namespace Munarium.Sources;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// A source's address: which tenant, which logical path, and the hash of the bytes that path holds.
/// </summary>
/// <remarks>
/// A <em>source</em> is a raw document the retrieval tier indexes, and its identity is the logical path -
/// the caller-supplied filename - and not its content hash. That distinction is load-bearing: a collection
/// binds by path prefix, so a document uploaded to two prefixes has to be two sources; and the same bytes
/// may legitimately live at two paths, each with its own rebuild and retirement schedule.
/// <para>
/// <see cref="ContentHash"/> remains as <em>integrity</em>: it is verified against any declared sha-256
/// before commit, recorded on the row, and surfaced in provenance. It is simply not the primary key.
/// </para>
/// <para>
/// Constructing a key validates the path, so every caller that reaches the object store has already been
/// through the check.
/// </para>
/// </remarks>
public sealed record SourceKey
{
    /// <summary>Gets the tenant the path belongs to.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the logical path of the document.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the hash of the bytes this path currently holds.</summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Creates a validated key.
    /// </summary>
    /// <param name="tenant">The tenant; non-empty, and without a slash.</param>
    /// <param name="path">The logical path.</param>
    /// <param name="contentHash">The hash of the bytes at that path.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException">Thrown when the tenant or the path is not addressable.</exception>
    public static SourceKey New(string tenant, string path, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        SourcePaths.Validate(path);
        RequireAddressableTenant(tenant);

        return new SourceKey { Tenant = tenant, Path = path, ContentHash = contentHash };
    }

    private static void RequireAddressableTenant(string tenant)
    {
        if (tenant.Length == 0 || tenant.Contains('/'))
        {
            throw new ArgumentException(
                $"tenant '{tenant}' must be non-empty and must not contain '/'",
                nameof(tenant));
        }
    }

    /// <summary>
    /// Derives a source identity from a tenant and a path.
    /// </summary>
    /// <remarks>
    /// Deterministic, stable, and derivable client-side without a round trip: the same tenant and path give
    /// the same identity everywhere, which is what lets a caller name a source it has not uploaded yet.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="path">The logical path.</param>
    /// <returns>The identity, as <c>src-</c> plus the first 16 hex characters of the SHA-256 of
    /// <c>tenant/path</c>.</returns>
    public static string Id(string tenant, string path)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(path);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(tenant, "/", path)));
        return string.Concat("src-", Convert.ToHexStringLower(digest)[..16]);
    }

    /// <summary>Gets this source's identity.</summary>
    public string SourceId => Id(Tenant, Path);

    /// <summary>
    /// Gets the blob name: <c>tenant/path</c>, the one place the two are composed.
    /// </summary>
    /// <remarks>
    /// One place on purpose: a second composition elsewhere is how a tenant prefix gets escaped from.
    /// </remarks>
    public string BlobName => string.Concat(Tenant, "/", Path);
}
