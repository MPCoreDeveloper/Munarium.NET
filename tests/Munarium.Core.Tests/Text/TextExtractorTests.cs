namespace Munarium.Core.Tests.Text;

using Munarium.Text;

/// <summary>
/// Tests for reading a document's text: the media types this port can read, and the ones it refuses rather than
/// guesses at.
/// </summary>
public class TextExtractorTests
{
    [Theory]
    [InlineData("text/plain")]
    [InlineData("TEXT/PLAIN")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("text/markdown")]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/xml")]
    [InlineData("application/ld+json")]
    [InlineData("image/svg+xml")]
    public void ATextualMediaTypeIsRead(string mediaType)
    {
        Assert.True(TextExtractor.CanExtract(mediaType));
        Assert.Equal("The Bell rang twice.", TextExtractor.Extract(mediaType, "The Bell rang twice."u8.ToArray()));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("image/png")]
    public void AMediaTypeWithNoExtractorIsRefusedByName(string mediaType)
    {
        Assert.False(TextExtractor.CanExtract(mediaType));

        var refusal = Assert.Throws<ArgumentException>(
            () => TextExtractor.Extract(mediaType, new byte[] { 0x25, 0x50, 0x44, 0x46 }));

        // The refusal has to name the type it could not read, or an operator cannot tell which of several documents
        // was refused and why.
        Assert.Contains(mediaType, refusal.Message, StringComparison.Ordinal);
        Assert.Equal("mediaType", refusal.ParamName);
    }

    /// <summary>
    /// A byte-order mark left in place would begin the first chunk with a zero-width character that the analyzer would
    /// then treat as part of the first term - the kind of difference that only shows up as a query that cannot find a
    /// document it can see.
    /// </summary>
    [Fact]
    public void AByteOrderMarkIsDropped()
    {
        var withMark = System.Text.Encoding.UTF8.GetPreamble().Concat("The Bell rang twice."u8.ToArray()).ToArray();

        var text = TextExtractor.Extract("text/plain", withMark);

        Assert.Equal("The Bell rang twice.", text);
        Assert.DoesNotContain('\uFEFF', text);
    }

    [Fact]
    public void TextIsReadAsUtf8()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Het luiden van de bel — een observatie.");

        Assert.Equal("Het luiden van de bel — een observatie.", TextExtractor.Extract("text/plain", bytes));
    }

    [Fact]
    public void AnEmptyMediaTypeIsRefused()
    {
        Assert.ThrowsAny<ArgumentException>(() => TextExtractor.CanExtract("  "));
    }
}
