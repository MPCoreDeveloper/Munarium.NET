namespace Munarium.Evidence;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// A thing whose content can be checked against the hash that names it.
/// </summary>
/// <remarks>
/// The seam a store or an index adapter implements, so content verification is one rule everywhere rather than a
/// copy of it per backend: a sealed artifact and a built index are both bytes somebody hashed and then promised.
/// </remarks>
public interface IVerifiableArtifact
{
    /// <summary>Gets the canonical hash of the content.</summary>
    string ArtifactHash { get; }

    /// <summary>Gets the content's length in bytes.</summary>
    long BytesLength { get; }
}

/// <summary>
/// Canonical content hashing for the evidence plane.
/// </summary>
/// <remarks>
/// One algorithm and one spelling: <c>sha256:</c> plus lowercase hex, which is the shape
/// <see cref="EvidenceContract.IsHash"/> accepts. A hash that differed in case or in prefix would be a hash two
/// implementations could disagree about without either of them being wrong.
/// </remarks>
public static class ArtifactContent
{
    /// <summary>Hashes bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The canonical hash.</returns>
    public static string Hash(ReadOnlySpan<byte> bytes) =>
        string.Concat("sha256:", Convert.ToHexStringLower(SHA256.HashData(bytes)));

    /// <summary>Hashes text as UTF-8.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The canonical hash.</returns>
    public static string Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Hash(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Checks bytes against the artifact that names them.
    /// </summary>
    /// <param name="artifact">What the bytes are supposed to be.</param>
    /// <param name="bytes">The bytes as they were read.</param>
    /// <returns>What the check found, including the reason when it failed.</returns>
    public static ArtifactVerification Verify(IVerifiableArtifact artifact, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        return new ArtifactVerification
        {
            ExpectedHash = artifact.ArtifactHash,
            ActualHash = Hash(bytes),
            ExpectedLength = artifact.BytesLength,
            ActualLength = bytes.Length,
        };
    }
}

/// <summary>
/// What a content check found, and what it compared.
/// </summary>
/// <remarks>
/// It reports the two hashes rather than a bare yes: an operator chasing a mismatch needs to know what was expected
/// and what arrived, and a boolean with no evidence behind it is what makes a verification step something people
/// learn to ignore.
/// </remarks>
public sealed record ArtifactVerification
{
    /// <summary>Gets the hash the artifact names.</summary>
    public required string ExpectedHash { get; init; }

    /// <summary>Gets the hash of the bytes that arrived.</summary>
    public required string ActualHash { get; init; }

    /// <summary>Gets the length the artifact names.</summary>
    public required long ExpectedLength { get; init; }

    /// <summary>Gets the length that arrived.</summary>
    public required long ActualLength { get; init; }

    /// <summary>Gets a value indicating whether the bytes are the ones the artifact names.</summary>
    public bool Verified =>
        ExpectedLength == ActualLength && string.Equals(ExpectedHash, ActualHash, StringComparison.Ordinal);

    /// <summary>Gets why the check failed, or <see langword="null"/> when it verified.</summary>
    /// <remarks>
    /// Length first: it is the more actionable fact, and a hash mismatch on a truncated artifact would say only
    /// that something is wrong without saying what.
    /// </remarks>
    public string? Failure => (ExpectedLength == ActualLength, Verified) switch
    {
        (false, _) => $"the content is {ActualLength} byte(s) long, but the artifact names {ExpectedLength}",
        (_, true) => null,
        _ => "the content does not hash to the value the artifact names",
    };
}
