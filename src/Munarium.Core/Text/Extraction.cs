namespace Munarium.Text;

/// <summary>How a source's text was obtained.</summary>
/// <remarks>
/// Recorded rather than assumed, because an OCR'd document and a text-layer one are not equivalent evidence: the source
/// row stores this name, so anything reasoning over citations can tell which it got. The names are the original's, and
/// they are what an index version's extractor set and a source row both carry.
/// </remarks>
public enum ExtractionMethod
{
    /// <summary>The bytes were already text: markdown, plain text, CSV.</summary>
    Text = 0,

    /// <summary>A DOCX's body was read out of its own XML.</summary>
    Docx = 1,

    /// <summary>A PDF's embedded text layer was read.</summary>
    PdfTextLayer = 2,

    /// <summary>
    /// A model read the pages. Not ported here: see <see cref="TextExtractor"/> for the two routes upstream has and the
    /// one this port does not, and the scan half that already works.
    /// </summary>
    Ocr = 3,
}

/// <summary>What happened when a source was extracted.</summary>
/// <remarks>
/// <see cref="Empty"/> is a first-class outcome and not an error. The original calls this field "the invisible-document
/// signal": a scanned document has no text layer, and the row has to say so rather than letting the corpus quietly hold
/// a document that contributes no chunks.
/// </remarks>
public enum ExtractionStatus
{
    /// <summary>Text came out.</summary>
    Ok = 0,

    /// <summary>The bytes were recognised and yielded nothing usable.</summary>
    Empty = 1,

    /// <summary>The bytes claimed a format this port reads and were not it.</summary>
    Failed = 2,
}

/// <summary>The names a source row and the wire use for extraction outcomes.</summary>
/// <remarks>
/// The row stores names and not ordinals, so a status is readable in the table and a new method does not renumber every
/// row written before it - which is the same reason the original stores these as strings.
/// </remarks>
public static class ExtractionNames
{
    /// <summary>Gets the name a method is stored and served under.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The name.</returns>
    public static string ToWireName(this ExtractionMethod method) => method switch
    {
        ExtractionMethod.Docx => "docx",
        ExtractionMethod.PdfTextLayer => "pdf-text",
        ExtractionMethod.Ocr => "ocr",
        _ => "text",
    };

    /// <summary>Gets the name a status is stored and served under.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The name.</returns>
    public static string ToWireName(this ExtractionStatus status) => status switch
    {
        ExtractionStatus.Empty => "empty",
        ExtractionStatus.Failed => "failed",
        _ => "ok",
    };

    /// <summary>Reads a method back out of a row.</summary>
    /// <param name="name">The stored name, or <see langword="null"/>.</param>
    /// <returns>The method, or <see langword="null"/> when the row carries none.</returns>
    public static ExtractionMethod? MethodFrom(string? name) => name switch
    {
        "text" => ExtractionMethod.Text,
        "docx" => ExtractionMethod.Docx,
        "pdf-text" => ExtractionMethod.PdfTextLayer,
        "ocr" => ExtractionMethod.Ocr,
        _ => null,
    };

    /// <summary>Reads a status back out of a row.</summary>
    /// <param name="name">The stored name, or <see langword="null"/>.</param>
    /// <returns>The status, or <see langword="null"/> when the row carries none.</returns>
    public static ExtractionStatus? StatusFrom(string? name) => name switch
    {
        "ok" => ExtractionStatus.Ok,
        "empty" => ExtractionStatus.Empty,
        "failed" => ExtractionStatus.Failed,
        _ => null,
    };
}

/// <summary>One source's extracted text, and how it was obtained.</summary>
/// <remarks>
/// The text travels with its provenance rather than alone, because the two halves are decided together: which extractor
/// ran is what decides whether the outcome is usable, and a caller that has to guess the method from the media type
/// would be guessing wrong for a scan - which is a PDF whose text layer turned out to be absent.
/// </remarks>
/// <param name="Text">The text, empty when nothing usable came out.</param>
/// <param name="Status">What happened.</param>
/// <param name="Method">How the text was obtained.</param>
public sealed record Extracted(string Text, ExtractionStatus Status, ExtractionMethod Method)
{
    /// <summary>Builds an outcome from text that was read.</summary>
    /// <remarks>
    /// Whitespace-only output is <see cref="ExtractionStatus.Empty"/> in every way that matters: it produces no chunks,
    /// so calling it <c>ok</c> would hide the miss.
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <param name="method">How it was obtained.</param>
    /// <returns>The outcome.</returns>
    public static Extracted Ok(string text, ExtractionMethod method) => new(
        text,
        string.IsNullOrWhiteSpace(text) ? ExtractionStatus.Empty : ExtractionStatus.Ok,
        method);

    /// <summary>Builds an outcome for bytes that were recognised and yielded nothing usable.</summary>
    /// <param name="method">How they were read.</param>
    /// <returns>The outcome.</returns>
    public static Extracted Empty(ExtractionMethod method) =>
        new(string.Empty, ExtractionStatus.Empty, method);

    /// <summary>Builds an outcome for bytes that are not the format they claim.</summary>
    /// <param name="method">The extractor that refused them.</param>
    /// <returns>The outcome.</returns>
    public static Extracted Failed(ExtractionMethod method) =>
        new(string.Empty, ExtractionStatus.Failed, method);
}
