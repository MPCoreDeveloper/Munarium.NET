namespace Munarium.Sources;

using System.Text;

/// <summary>
/// The rules a source path has to satisfy, and the one keyspace documents may not use.
/// </summary>
/// <remarks>
/// A path is caller-supplied and the server never rewrites it, so this is a security boundary rather than
/// tidiness: a path that escapes its tenant prefix, or that composes an ambiguous blob name, would let one
/// tenant's document be addressed - or overwritten - as another's.
/// <para>
/// The length bound is counted in <em>bytes</em>, not characters, because that is the unit the bound was
/// chosen in: a path of 600 two-byte characters is 1200 bytes and does not fit.
/// </para>
/// </remarks>
public static class SourcePaths
{
    /// <summary>
    /// The keyspace sealed evidence artifacts live under.
    /// </summary>
    /// <remarks>
    /// Declared here because this is where it is enforced; the evidence plane owns it, and the constant
    /// moves there when that plane is ported rather than being written twice.
    /// </remarks>
    public const string EvidencePrefix = "evidence/";

    private const int MaxBytes = 1024;

    /// <summary>
    /// Validates a source path.
    /// </summary>
    /// <param name="path">The caller-supplied path.</param>
    /// <exception cref="ArgumentException">Thrown when the path could escape its tenant or address a second blob.</exception>
    public static void Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == 0)
        {
            throw Invalid(path, "empty");
        }

        if (Encoding.UTF8.GetByteCount(path) > MaxBytes)
        {
            throw Invalid(path, "longer than 1024 bytes");
        }

        if (path.StartsWith('/'))
        {
            throw Invalid(path, "absolute paths are not allowed");
        }

        if (path.Contains('\\'))
        {
            throw Invalid(path, "backslashes are not allowed; use '/' separators");
        }

        if (path.Contains('\0'))
        {
            throw Invalid(path, "contains a NUL byte");
        }

        // A Windows drive-letter path ("C:/docs/x.md") is absolute in a way the leading-slash check misses.
        if (path.Length >= 2 && path[1] == ':')
        {
            throw Invalid(path, "drive-qualified paths are not allowed");
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0)
            {
                throw Invalid(path, "contains an empty path segment");
            }

            if (segment is "." or "..")
            {
                throw Invalid(path, "contains a '.' or '..' segment");
            }
        }
    }

    /// <summary>
    /// Refuses a <em>document</em> path under the reserved evidence keyspace.
    /// </summary>
    /// <remarks>
    /// Sealed artifacts share this object store, so a document at <c>evidence/…</c> could collide with an
    /// artifact's blob - and, worse, could tempt a reader into inferring authorization from a path.
    /// Authorization comes from the evidence row, never from where the bytes sit; reserving the prefix is
    /// what keeps that true rather than merely intended.
    /// <para>
    /// Deliberately not folded into <see cref="Validate"/>: the evidence writer builds legitimate keys under
    /// this prefix, and a check it had to bypass would be a back door in the one place a back door is least
    /// affordable. This is a separate predicate that only document ingress calls.
    /// </para>
    /// </remarks>
    /// <param name="path">The caller-supplied path.</param>
    /// <exception cref="ArgumentException">Thrown when the path is under the reserved prefix.</exception>
    public static void RefuseReservedDocumentPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.StartsWith(EvidencePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"source path '{path}' is under the reserved '{EvidencePrefix}' prefix; that keyspace "
                    + "belongs to sealed evidence artifacts and cannot hold documents",
                nameof(path));
        }
    }

    private static ArgumentException Invalid(string path, string detail) =>
        new($"source path '{path}' is invalid: {detail}", nameof(path));
}
