namespace Munarium.Core.Tests.Evidence;

using System.Text;
using Munarium.Evidence;
using static Munarium.Core.Tests.Support.ManifestFixture;

/// <summary>
/// Tests for content verification: the bytes an artifact names are the bytes a reader gets, or the check says why
/// they are not.
/// </summary>
public class ArtifactContentTests
{
    /// <summary>The published SHA-256 of <c>abc</c>, so the algorithm and its spelling are pinned rather than assumed.</summary>
    [Fact]
    public void TheHashIsTheCanonicalSha256TheContractAccepts() =>
        Assert.Equal(
            "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ArtifactContent.Hash("abc"));

    [Fact]
    public void TheHashSpellingIsTheOneTheContractValidates()
    {
        var hash = ArtifactContent.Hash([1, 2, 3]);

        Assert.True(EvidenceContract.IsHash(hash));
        Assert.Equal(71, hash.Length);
        Assert.Equal(hash, ArtifactContent.Hash([1, 2, 3]));
        Assert.NotEqual(hash, ArtifactContent.Hash([1, 2, 4]));
    }

    [Fact]
    public void TextIsHashedAsItsUtf8Bytes() =>
        Assert.Equal(ArtifactContent.Hash(Encoding.UTF8.GetBytes("é")), ArtifactContent.Hash("é"));

    [Fact]
    public void AnArtifactWhoseContentIsUntouchedVerifies()
    {
        var bytes = Encoding.UTF8.GetBytes("region,total\nEMEA,12\n");

        var verification = ArtifactContent.Verify(Sealed(bytes), bytes);

        Assert.True(verification.Verified);
        Assert.Null(verification.Failure);
        Assert.Equal(verification.ExpectedHash, verification.ActualHash);
    }

    [Fact]
    public void ContentThatChangedDoesNotVerifyAndSaysWhy()
    {
        var verification = ArtifactContent.Verify(
            Sealed(Encoding.UTF8.GetBytes("EMEA,12\n")),
            Encoding.UTF8.GetBytes("EMEA,15\n"));

        Assert.False(verification.Verified);
        Assert.Equal("the content does not hash to the value the artifact names", verification.Failure);
        Assert.Equal(verification.ExpectedLength, verification.ActualLength);
    }

    /// <summary>
    /// Truncated content is the case a reader most needs named: the two lengths are what tell them what happened,
    /// and a hash mismatch alone would say only that something is wrong.
    /// </summary>
    [Fact]
    public void TruncatedContentDoesNotVerifyAndReportsTheLengths()
    {
        var bytes = Encoding.UTF8.GetBytes("EMEA,12\nAPAC,7\n");

        var verification = ArtifactContent.Verify(Sealed(bytes), bytes.AsSpan(0, 9));

        Assert.False(verification.Verified);
        Assert.Equal("the content is 9 byte(s) long, but the artifact names 15", verification.Failure);
    }

    /// <summary>
    /// The verification seam is what a store or an index adapter implements, and the manifest is already one - so
    /// the rule is stated once rather than copied per backend.
    /// </summary>
    [Fact]
    public void AManifestIsAVerifiableArtifact()
    {
        var bytes = Encoding.UTF8.GetBytes("region,total\nEMEA,12\n");
        IVerifiableArtifact artifact = Sealed(bytes);

        Assert.True(ArtifactContent.Verify(artifact, bytes).Verified);
        Assert.False(ArtifactContent.Verify(artifact, bytes.AsSpan(..^1)).Verified);
    }

    private static EvidenceManifest Sealed(byte[] bytes) =>
        Manifest() with { ArtifactHash = ArtifactContent.Hash(bytes), BytesLength = bytes.Length };
}
