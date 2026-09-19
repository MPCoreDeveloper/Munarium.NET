namespace Munarium.Core.Tests.Text;

using System.IO.Compression;
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

    /// <summary>
    /// The DOCX word-processing type is read, out of its own XML.
    /// </summary>
    /// <remarks>
    /// This is the one media type beyond text that this port reads, and the claim it corrects is in the seam's own
    /// documentation: upstream reads DOCX too, and no model is involved in that - a <c>.docx</c> is a zip of XML. What is
    /// genuinely out of reach is a PDF, whose text layer needs a parser this port does not have.
    /// </remarks>
    [Fact]
    public void TheDocxMediaTypeIsRead()
    {
        Assert.True(TextExtractor.CanExtract(DocxExtractor.Media));

        var text = TextExtractor.Extract(
            DocxExtractor.Media,
            Docx("""<w:document xmlns:w="x"><w:body><w:p><w:r><w:t>Read me</w:t></w:r></w:p></w:body></w:document>"""));

        Assert.Equal("Read me", text);
    }

    /// <summary>The extractor set's version names every extractor it covers, which is what an index identity hashes.</summary>
    [Fact]
    public void TheVersionCoversTheRegisteredSet()
    {
        var version = TextExtractor.Version();

        Assert.StartsWith("extract@1[", version, StringComparison.Ordinal);
        Assert.Contains("docx@1", version, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("application/pdf")]
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

    /// <summary>A DOCX comes out as blank-line-separated paragraphs, with split runs rejoined.</summary>
    /// <remarks>
    /// Word splits a sentence across runs, so the text has to come back whole; paragraphs are blank-line separated because
    /// that is exactly what the chunker splits on.
    /// </remarks>
    [Fact]
    public void ADocxExtractsParagraphsAndJoinsSplitRuns()
    {
        var text = DocxExtractor.Read(Docx(
            """
            <?xml version="1.0"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
             <w:body>
              <w:p><w:r><w:t>Vacation Policy</w:t></w:r></w:p>
              <w:p><w:r><w:t>Employees accrue </w:t></w:r><w:r><w:t>15 days</w:t></w:r></w:p>
              <w:p></w:p>
              <w:p><w:r><w:t>Line one</w:t></w:r><w:r><w:br/><w:t>line two</w:t></w:r></w:p>
             </w:body>
            </w:document>
            """));

        Assert.StartsWith("Vacation Policy\n\nEmployees accrue 15 days", text, StringComparison.Ordinal);

        // An empty paragraph produces no block, and <w:br/> is a break inside one rather than a new paragraph.
        Assert.DoesNotContain("\n\n\n", text, StringComparison.Ordinal);
        Assert.Contains("Line one\nline two", text, StringComparison.Ordinal);
    }

    /// <summary>A field instruction is markup, not prose: a table of contents code is not a sentence.</summary>
    [Fact]
    public void FieldInstructionsAreNotProse() =>
        Assert.Equal(
            "Real text",
            DocxExtractor.Read(Docx(
                """
                <w:document xmlns:w="x"><w:body>
                  <w:p><w:r><w:instrText>TOC \o "1-3"</w:instrText></w:r><w:r><w:t>Real text</w:t></w:r></w:p>
                </w:body></w:document>
                """)));

    /// <summary>A document written with a different namespace prefix parses the same, which is common in the wild.</summary>
    [Fact]
    public void NamespacePrefixesStillParse() =>
        Assert.Equal(
            "Prefixed",
            DocxExtractor.Read(Docx(
                """
                <ns0:document xmlns:ns0="x"><ns0:body>
                  <ns0:p><ns0:r><ns0:t>Prefixed</ns0:t></ns0:r></ns0:p>
                </ns0:body></ns0:document>
                """)));

    /// <summary>A document with nothing in it reads as nothing rather than as markup.</summary>
    [Fact]
    public void AnEmptyDocumentReadsAsNothing() =>
        Assert.Equal(
            string.Empty,
            DocxExtractor.Read(Docx("""<w:document xmlns:w="x"><w:body></w:body></w:document>""")));

    /// <summary>A zip without the document part is refused: it claims to be a document and is not one.</summary>
    [Fact]
    public void AZipWithoutTheDocumentPartIsRefused()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("other.txt").Open();

            entry.Write("hi"u8);
        }

        var refusal = Assert.Throws<ArgumentException>(() => DocxExtractor.Read(buffer.ToArray()));

        Assert.Contains("word/document.xml", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A file that is not a zip at all is refused rather than thrown as something unexpected.</summary>
    [Fact]
    public void ANonZipIsRefused() =>
        Assert.Contains(
            "docx@1 could not read it",
            Assert.Throws<ArgumentException>(() => DocxExtractor.Read("not a zip at all"u8.ToArray())).Message,
            StringComparison.Ordinal);

    /// <summary>Builds a DOCX around one document part, which is all the extractor reads.</summary>
    /// <param name="documentXml">The document XML.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Docx(string documentXml)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("word/document.xml").Open();

            entry.Write(System.Text.Encoding.UTF8.GetBytes(documentXml));
        }

        return buffer.ToArray();
    }
}
