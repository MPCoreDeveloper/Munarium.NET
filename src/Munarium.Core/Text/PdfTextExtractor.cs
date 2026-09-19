namespace Munarium.Text;

using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

/// <summary>Reads a PDF's text layer.</summary>
/// <remarks>
/// Pure managed, through PdfPig: no native library, no rasterizer, no model, and no runtime code generation - so this
/// survives a NativeAOT publish, which <c>tools/Munarium.AotSmoke</c> proves per platform rather than asserts.
/// <para>
/// This reads the <em>text layer only</em>: the characters the producer actually embedded. A scanned document has none and
/// comes back empty, which is not a failure to hide but the exact signal the OCR path keys on - and a page whose font
/// carries no Unicode mapping at all yields the font's own codes instead of words, which is a property of that file that
/// no text-layer reader can fix, only a reader that looks at the pixels can.
/// </para>
/// </remarks>
public static class PdfTextExtractor
{
    /// <summary>The media type a PDF declares.</summary>
    public const string Media = "application/pdf";

    /// <summary>The extractor's identity, which joins an index version's identity.</summary>
    public const string Id = "pdf-text@1";

    /// <summary>
    /// Below this many non-whitespace characters a "text layer" is almost certainly page furniture - a stamped page
    /// number, a scanner watermark - rather than content, so it is treated as absent and OCR gets its turn.
    /// </summary>
    private const int MinimumMeaningfulChars = 16;

    /// <summary>Reads a PDF's text layer.</summary>
    /// <param name="bytes">The document's bytes.</param>
    /// <returns>The text, or the outcome that says why there is none.</returns>
    /// <remarks>The original's rule decides the shape here: a PDF with no usable text layer is a scan, which is a real and
    /// expected document, so it comes back <see cref="ExtractionStatus.Empty"/> with the method that failed to find a
    /// layer - and a PDF that cannot be parsed at all is <see cref="ExtractionStatus.Failed"/>, not an exception.</remarks>
    public static Extracted Read(ReadOnlyMemory<byte> bytes)
    {
        string raw;

        try
        {
            using var document = PdfDocument.Open(bytes.ToArray());

            var pages = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page);

                if (text.Length == 0)
                {
                    continue;
                }

                // A page break is a paragraph break: it is one of the few boundaries a PDF states outright, and it is
                // what the chunker splits on.
                if (pages.Length > 0)
                {
                    pages.Append("\n\n");
                }

                pages.Append(text);
            }

            raw = pages.ToString();
        }
        catch (Exception)
        {
            // A caller-uploaded PDF must never take the indexer down, and the outcome says which it was: PdfPig is a
            // parser over whatever bytes a caller sends, and the original contains the same hazard by catching panics
            // around its own parser. Failed is what the row records and what a build refuses on.
            return Extracted.Failed(ExtractionMethod.PdfTextLayer);
        }

        var body = Normalize(raw);

        // Below the threshold is empty and not failed, which is the original's distinction: a scan is a real document
        // with no text layer, and OCR - the path for it - is the capability this port does not have.
        return CountMeaningful(body) < MinimumMeaningfulChars
            ? Extracted.Empty(ExtractionMethod.PdfTextLayer)
            : Extracted.Ok(body, ExtractionMethod.PdfTextLayer);
    }

    /// <summary>Counts the characters that are not whitespace, which is what "usable text" means here.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The count.</returns>
    private static int CountMeaningful(string text) => text.Count(character => !char.IsWhiteSpace(character));

    /// <summary>
    /// Rejoins the hard line wraps a PDF's text layer arrives with.
    /// </summary>
    /// <remarks>
    /// A text layer breaks lines at the column width, so a sentence is spread over lines that are not paragraphs.
    /// Single newlines inside a block become spaces so sentences survive, while blank lines are kept - those are the
    /// paragraph boundaries the chunker splits on.
    /// </remarks>
    /// <param name="raw">The text as read.</param>
    /// <returns>The text with sentences whole.</returns>
    private static string Normalize(string raw)
    {
        var body = new StringBuilder(raw.Length);
        var blanks = 0;

        foreach (var line in raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimEnd();

            if (trimmed.TrimStart().Length == 0)
            {
                blanks++;
                continue;
            }

            if (body.Length > 0)
            {
                body.Append(blanks > 0 ? "\n\n" : " ");
            }

            blanks = 0;
            body.Append(trimmed.TrimStart());
        }

        return body.ToString();
    }
}
