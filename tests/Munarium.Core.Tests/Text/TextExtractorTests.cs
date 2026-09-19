namespace Munarium.Core.Tests.Text;

using System.IO.Compression;
using System.Text;
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
        Assert.Equal("The Bell rang twice.", Text(mediaType, "The Bell rang twice."u8.ToArray()));
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

        var text = Text(
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
        Assert.Contains("pdf-text@1", version, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PDF's text layer is read, and the media type is one this port answers for.
    /// </summary>
    /// <remarks>
    /// The layer is read through PdfPig, which is pure managed. This proves the seam answers; the AOT smoke tool proves
    /// the same read happens inside a NativeAOT binary on every platform CI publishes, which is where the risk was.
    /// </remarks>
    [Fact]
    public void ThePdfMediaTypeIsRead()
    {
        Assert.True(TextExtractor.CanExtract(PdfTextExtractor.Media));

        var text = Text(
            PdfTextExtractor.Media,
            Pdf("BT /F1 12 Tf 72 720 Td (The quarterly settlement was approved on 14 March) Tj ET"));

        Assert.Equal("The quarterly settlement was approved on 14 March", text);
    }

    /// <summary>
    /// A text layer breaks lines at the column width, so the lines of a sentence are not paragraphs.
    /// </summary>
    /// <remarks>
    /// This is the original's rule, carried over: single newlines inside a block become spaces so a sentence survives the
    /// wrap, while blank lines are kept because those are the boundaries the chunker splits on.
    /// </remarks>
    [Fact]
    public void HardWrapsAreRejoined()
    {
        var text = Text(
            PdfTextExtractor.Media,
            Pdf("BT /F1 12 Tf 72 720 Td (The quarterly settlement was) Tj 0 -14 Td (approved on 14 March) Tj ET"));

        Assert.Equal("The quarterly settlement was approved on 14 March", text);
    }

    /// <summary>
    /// A PDF with no text layer reads as nothing: a scan is what the OCR path is for, and this port does not have one.
    /// </summary>
    [Fact]
    public void APdfWithoutATextLayerReadsAsNothing()
    {
        Assert.Equal(string.Empty, Text(PdfTextExtractor.Media, Pdf(string.Empty)));

        // Page furniture is not content either: a stamped page number produces no chunk worth citing.
        Assert.Equal(
            string.Empty,
            Text(PdfTextExtractor.Media, Pdf("BT /F1 12 Tf 72 720 Td (12) Tj ET")));
    }

    /// <summary>A PDF that cannot be parsed is a failed extraction, which is what the row records.</summary>
    [Fact]
    public void APdfThatCannotBeParsedIsAFailedExtraction()
    {
        var failed = TextExtractor.Extract(PdfTextExtractor.Media, "%PDF-1.7 not really a pdf"u8.ToArray());

        Assert.Equal(string.Empty, failed.Text);
        Assert.Equal(ExtractionStatus.Failed, failed.Status);
        Assert.Equal(ExtractionMethod.PdfTextLayer, failed.Method);
    }

    [Theory]
    [InlineData("image/png")]
    public void AMediaTypeWithNoExtractorIsRefusedByName(string mediaType)
    {
        Assert.False(TextExtractor.CanExtract(mediaType));

        var refusal = Assert.Throws<ArgumentException>(
            () => Text(mediaType, new byte[] { 0x25, 0x50, 0x44, 0x46 }));

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

        var text = Text("text/plain", withMark);

        Assert.Equal("The Bell rang twice.", text);
        Assert.DoesNotContain('\uFEFF', text);
    }

    [Fact]
    public void TextIsReadAsUtf8()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Het luiden van de bel — een observatie.");

        Assert.Equal("Het luiden van de bel — een observatie.", Text("text/plain", bytes));
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
        var text = Read(Docx(
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
            Read(Docx(
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
            Read(Docx(
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
            Read(Docx("""<w:document xmlns:w="x"><w:body></w:body></w:document>""")));

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

        var failed = DocxExtractor.Read(buffer.ToArray());

        Assert.Equal(ExtractionStatus.Failed, failed.Status);
        Assert.Equal(ExtractionMethod.Docx, failed.Method);
    }

    /// <summary>A DOCX with nothing in it is an empty extraction, and the row can say which extractor found nothing.</summary>
    [Fact]
    public void ADocxWithNothingInItIsAnEmptyExtraction()
    {
        var empty = DocxExtractor.Read(Docx("""<w:document xmlns:w="x"><w:body></w:body></w:document>"""));

        Assert.Equal(Extracted.Empty(ExtractionMethod.Docx), empty);
    }

    /// <summary>A file that is not a zip at all is a failed extraction rather than something unexpected.</summary>
    [Fact]
    public void ANonZipIsAFailedExtraction()
    {
        var failed = DocxExtractor.Read("not a zip at all"u8.ToArray());

        Assert.Equal(ExtractionStatus.Failed, failed.Status);
        Assert.Equal(ExtractionMethod.Docx, failed.Method);
    }

    /// <summary>
    /// Builds a one-page PDF around a content stream, written by hand so the fixture is deterministic and needs no
    /// generator - the same shape the original's own PDF test uses.
    /// </summary>
    /// <param name="contentStream">The page's content stream operators.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Pdf(string contentStream)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> "
                + "/Contents 4 0 R >>",
            $"<< /Length {contentStream.Length} >>\nstream\n{contentStream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();

        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xrefAt = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:0000000000} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefAt}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>Reads a document's text, for the cases that only care about the words.</summary>
    /// <param name="mediaType">The media type.</param>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The text.</returns>
    private static string Text(string mediaType, ReadOnlyMemory<byte> bytes) =>
        TextExtractor.Extract(mediaType, bytes).Text;

    /// <summary>Reads a DOCX's text, for the same reason.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The text.</returns>
    private static string Read(byte[] bytes) => DocxExtractor.Read(bytes).Text;

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
