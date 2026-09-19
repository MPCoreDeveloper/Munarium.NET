namespace Munarium.Text;

using System.IO.Compression;
using System.Text;
using System.Xml;

/// <summary>Reads a DOCX's body out of its own XML.</summary>
/// <remarks>
/// A <c>.docx</c> is a zip whose <c>word/document.xml</c> holds the body: text in <c>w:t</c> runs, paragraphs in
/// <c>w:p</c>, and explicit <c>w:br</c> and <c>w:tab</c>. Reading the XML directly rather than through a document model
/// keeps the paragraph boundaries - the ones the chunker splits on - exactly where Word put them, and elements are matched
/// by their local name so a document written with any namespace prefix parses the same.
/// </remarks>
public static class DocxExtractor
{
    /// <summary>The media type a DOCX declares.</summary>
    public const string Media = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>The extractor's identity, which joins an index version's identity.</summary>
    public const string Id = "docx@1";

    /// <summary>
    /// Guard against a zip bomb: a body far past this is not a document anyone is retrieving over, it is an attack on the
    /// indexer.
    /// </summary>
    private const long MaxDocumentXml = 128L * 1024 * 1024;

    /// <summary>Reads a DOCX's text.</summary>
    /// <param name="bytes">The document's bytes.</param>
    /// <returns>The text with paragraphs blank-line separated, or the outcome that says why there is none.</returns>
    /// <remarks>A file that claims to be a DOCX and is not comes back <see cref="ExtractionStatus.Failed"/> rather than as
    /// an exception: whose fault it is belongs to the document, and the row has to be able to say so.</remarks>
    public static Extracted Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray());
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var entry = archive.GetEntry("word/document.xml")
                ?? throw new InvalidDataException("it has no word/document.xml");

            // The declared size is a cheap pre-filter; the bounded read below is the guard. The declared size is the
            // archive author's claim and the reader never enforces it, so a crafted entry declaring a hundred bytes over a
            // deflate stream that inflates to gigabytes stops one byte past the ceiling instead of allocating it.
            if (entry.Length > MaxDocumentXml)
            {
                throw new InvalidDataException("its document.xml is implausibly large");
            }

            using var document = entry.Open();

            return Extracted.Ok(Body(ReadBounded(document)), ExtractionMethod.Docx);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException or XmlException)
        {
            return Extracted.Failed(ExtractionMethod.Docx);
        }
    }

    /// <summary>Reads a stream as text, refusing a body that inflates past the ceiling.</summary>
    /// <param name="document">The stream.</param>
    /// <returns>The text.</returns>
    /// <exception cref="InvalidDataException">The body inflated past the ceiling.</exception>
    private static string ReadBounded(Stream document)
    {
        using var reader = new StreamReader(document, Encoding.UTF8);

        var buffer = new char[8192];
        var text = new StringBuilder();
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            text.Append(buffer, 0, read);

            if (text.Length > MaxDocumentXml)
            {
                throw new InvalidDataException($"its document.xml inflates past {MaxDocumentXml} bytes");
            }
        }

        return text.ToString();
    }

    /// <summary>Walks the body, emitting one blank-line-separated block per paragraph.</summary>
    /// <remarks>
    /// An empty paragraph produces no block, because a blank line between nothing and nothing is not a chunk. A field
    /// instruction's text is markup rather than prose - a table of contents code is not a sentence - so its runs are
    /// skipped rather than indexed.
    /// </remarks>
    /// <param name="xml">The document XML.</param>
    /// <returns>The body's text.</returns>
    private static string Body(string xml)
    {
        var body = new StringBuilder();
        var paragraph = new StringBuilder();
        var inTextRun = false;
        var inInstruction = false;
        var hasText = false;

        using var reader = XmlReader.Create(
            new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreProcessingInstructions = true });

        while (reader.Read())
        {
            if (reader.NodeType is XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "p":
                        paragraph.Clear();
                        hasText = false;
                        break;

                    case "t":
                        inTextRun = true;
                        break;

                    case "instrText":
                        inInstruction = true;
                        break;

                    case "br" when reader.IsEmptyElement:
                        paragraph.Append('\n');
                        break;

                    case "tab" when reader.IsEmptyElement:
                        paragraph.Append('\t');
                        break;

                    default:
                        break;
                }

                continue;
            }

            if (reader.NodeType is XmlNodeType.Text)
            {
                if (inTextRun && !inInstruction)
                {
                    paragraph.Append(reader.Value);
                    hasText = true;
                }

                continue;
            }

            if (reader.NodeType is not XmlNodeType.EndElement)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "t":
                    inTextRun = false;
                    break;

                case "instrText":
                    inInstruction = false;
                    break;

                case "p":
                    Append(body, paragraph, hasText);
                    break;

                default:
                    break;
            }
        }

        return body.ToString();
    }

    /// <summary>Appends one paragraph as a block, when it has anything to say.</summary>
    /// <param name="body">The text so far.</param>
    /// <param name="paragraph">The paragraph.</param>
    /// <param name="hasText">Whether any run in it contributed text.</param>
    private static void Append(StringBuilder body, StringBuilder paragraph, bool hasText)
    {
        var line = paragraph.ToString().Trim();

        if (!hasText || line.Length == 0)
        {
            return;
        }

        if (body.Length > 0)
        {
            body.Append("\n\n");
        }

        body.Append(line);
    }
}
