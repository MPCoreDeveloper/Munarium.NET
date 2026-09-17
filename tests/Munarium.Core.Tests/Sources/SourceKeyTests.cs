namespace Munarium.Core.Tests.Sources;

using Munarium.Sources;

/// <summary>
/// Tests for source identity and the path rules: what a path may look like, and what a source's identity is
/// derived from.
/// </summary>
public class SourceKeyTests
{
    [Fact]
    public void AhnIdentityIsDeterministicAndPathScoped()
    {
        Assert.Equal(SourceKey.Id("demo", "a/b.md"), SourceKey.Id("demo", "a/b.md"));

        // The whole point: the same bytes at two paths are two sources.
        Assert.NotEqual(SourceKey.Id("demo", "a/b.md"), SourceKey.Id("demo", "c/b.md"));

        // And tenants never collide.
        Assert.NotEqual(SourceKey.Id("demo", "a/b.md"), SourceKey.Id("other", "a/b.md"));

        Assert.StartsWith("src-", SourceKey.Id("demo", "a/b.md"), StringComparison.Ordinal);
        Assert.Equal(20, SourceKey.Id("demo", "a/b.md").Length);
    }

    [Fact]
    public void AKeyCarriesItsOwnIdentity() =>
        Assert.Equal(
            SourceKey.Id("demo", "northgate/x.md"),
            SourceKey.New("demo", "northgate/x.md", "sha256:abc").SourceId);

    [Fact]
    public void ABlobNameIsTenantPrefixed() =>
        Assert.Equal("demo/northgate/03_finance/x.md", SourceKey.New("demo", "northgate/03_finance/x.md", "abc").BlobName);

    /// <summary>
    /// The collision this prevents: a document whose path is an artifact's blob name. Authorization is never
    /// inferred from a path, but a collision would still let one overwrite the other.
    /// </summary>
    [Fact]
    public void TheEvidenceKeyspaceIsRefusedForDocuments()
    {
        var error = Assert.Throws<ArgumentException>(() => SourcePaths.RefuseReservedDocumentPath("evidence/ev-abc"));
        Assert.Contains("reserved", error.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => SourcePaths.RefuseReservedDocumentPath("evidence/nested/x.md"));

        // Ordinary paths are untouched, including ones that merely MENTION evidence: the rule is a prefix,
        // not a substring.
        SourcePaths.RefuseReservedDocumentPath("northgate/evidence-summary.md");
        SourcePaths.RefuseReservedDocumentPath("evidencelike/x.md");
        SourcePaths.RefuseReservedDocumentPath("a/b/evidence/c.md");
    }

    /// <summary>
    /// A reserved prefix is refused as a document, but the same rule is not part of path validation: the
    /// evidence writer builds legitimate keys there, and a check it had to bypass would be a back door.
    /// </summary>
    [Fact]
    public void AReservedPathIsStillAValidPath()
    {
        SourcePaths.Validate("evidence/ev-abc");

        var key = SourceKey.New("demo", "evidence/ev-abc", "sha256:abc");
        Assert.Equal("demo/evidence/ev-abc", key.BlobName);
    }

    [Theory]
    [InlineData("../evil.md")]
    [InlineData("a/../../evil.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/windows/x.md")]
    [InlineData("a\\b.md")]
    [InlineData("a//b.md")]
    [InlineData("./a.md")]
    [InlineData("")]
    [InlineData("a/./b.md")]
    [InlineData("a/\0b.md")]
    public void TraversalAndAbsolutePathsAreRejected(string path) =>
        Assert.Throws<ArgumentException>(() => SourcePaths.Validate(path));

    [Theory]
    [InlineData("x.md")]
    [InlineData("northgate/03_finance/audited-fy2023.md")]
    [InlineData("rev/newspapers/sn83030214-1776-07-04.txt")]
    [InlineData("a.b/c-d_e/f.g.h")]
    public void OrdinaryPathsAreAccepted(string path) => SourcePaths.Validate(path);

    /// <summary>
    /// The bound is counted in bytes, because that is the unit it was chosen in: 600 two-byte characters do
    /// not fit where 600 ASCII characters do.
    /// </summary>
    [Fact]
    public void TheLengthBoundIsCountedInBytes()
    {
        SourcePaths.Validate(new string('a', 1024));
        Assert.Throws<ArgumentException>(() => SourcePaths.Validate(new string('a', 1025)));

        SourcePaths.Validate(new string('e', 512));

        var wide = new string('é', 600);
        Assert.Equal(1200, System.Text.Encoding.UTF8.GetByteCount(wide));
        Assert.Throws<ArgumentException>(() => SourcePaths.Validate(wide));
    }

    [Theory]
    [InlineData("")]
    [InlineData("demo/eu")]
    public void ATenantHasToBeAddressable(string tenant) =>
        Assert.Throws<ArgumentException>(() => SourceKey.New(tenant, "x.md", "h"));

    [Fact]
    public void AKeyRefusesAPathThatCouldEscapeItsTenant() =>
        Assert.Throws<ArgumentException>(() => SourceKey.New("demo", "../other/x.md", "h"));
}
