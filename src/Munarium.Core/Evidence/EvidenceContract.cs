namespace Munarium.Evidence;

using System.Buffers;
using System.Globalization;

/// <summary>
/// The evidence contract's fixed vocabulary: its version, its canonicalization, its bounds, and the shape a
/// hash has to have.
/// </summary>
/// <remarks>
/// These are constants rather than configuration because a manifest is a promise about bytes: a bound that a
/// deployment could raise is a bound a reader cannot rely on, and a hash format that varies is a hash nobody
/// can check.
/// </remarks>
public static class EvidenceContract
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdef");
    /// <summary>
    /// The contract version this server implements, mirroring the original's contract file.
    /// </summary>
    /// <remarks>
    /// Only the major is compared against a manifest: a major bump is a wire break, so the two trees have to
    /// be deployed together, while a minor one is not.
    /// </remarks>
    public const string Version = "1.0.0";

    /// <summary>The one canonicalization this contract major implements.</summary>
    public const string Canon = "canon@1";

    /// <summary>
    /// The keyspace sealed artifacts live under, and the one documents may not use.
    /// </summary>
    /// <remarks>
    /// The evidence plane owns this prefix and the source path rules enforce it, which is why the constant
    /// lives here rather than there: the writer that legitimately uses the keyspace is the one that has to
    /// define it.
    /// </remarks>
    public const string PathPrefix = "evidence/";

    /// <summary>Parquet: the accepted serialization for an artifact of any size.</summary>
    public const string MediaTypeParquet = "application/vnd.apache.parquet";

    /// <summary>CSV: the accepted serialization that stays readable without a Parquet reader.</summary>
    public const string MediaTypeCsv = "text/csv; charset=utf-8";

    /// <summary>At or below this size, bytes are sealed inline with the manifest.</summary>
    public const int InlineSealMaxBytes = 1024 * 1024;

    /// <summary>The largest artifact the plane will seal.</summary>
    public const long MaxArtifactBytes = 256L * 1024 * 1024;

    /// <summary>How many rows a resolution returns when the caller names no limit.</summary>
    public const int DefaultRowLimit = 100;

    /// <summary>The largest row limit a caller may name.</summary>
    public const int MaxRowLimit = 1000;

    /// <summary>
    /// How long an upload grant stays usable.
    /// </summary>
    /// <remarks>
    /// Short on purpose: a grant is a single-use capability to write bytes under an id the server has already
    /// committed to, so its blast radius is a function of its lifetime.
    /// </remarks>
    public const long GrantTtlSeconds = 900;

    /// <summary>
    /// Reports whether a value is a canonical content hash.
    /// </summary>
    /// <remarks>
    /// Lowercase hex and exactly sixty-four characters, because a hash that differs in case is a hash two
    /// implementations can disagree about without either being wrong.
    /// </remarks>
    /// <param name="value">The value to inspect.</param>
    /// <returns><see langword="true"/> when the value is <c>sha256:</c> plus sixty-four lowercase hex digits.</returns>
    public static bool IsHash(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept(HexDigits);

    /// <summary>
    /// Reports whether a declared contract version shares this server's major.
    /// </summary>
    /// <param name="declared">The version the manifest declares.</param>
    /// <returns><see langword="true"/> when the majors match.</returns>
    public static bool MajorMatches(string? declared) =>
        Major(declared) is { Length: > 0 } major &&
        string.Equals(major, Major(Version), StringComparison.Ordinal);

    /// <summary>
    /// Gets the major segment of a version.
    /// </summary>
    /// <param name="version">The version.</param>
    /// <returns>The major, or an empty string when there is none.</returns>
    public static string Major(string? version)
    {
        if (version is null)
        {
            return string.Empty;
        }

        var trimmed = version.Trim();
        var separator = trimmed.IndexOf('.', StringComparison.Ordinal);

        return separator < 0 ? trimmed : trimmed[..separator];
    }

    /// <summary>
    /// Formats a number the way the contract's identity material does.
    /// </summary>
    /// <param name="value">The number.</param>
    /// <returns>The invariant text.</returns>
    public static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
