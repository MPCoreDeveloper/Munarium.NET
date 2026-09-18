namespace Munarium.Text;

/// <summary>
/// Turns a document's bytes into the text an index can hold, for the media types this port can read.
/// </summary>
/// <remarks>
/// Upstream extracts PDFs and DOCX through a model capability this port has not ported, so this refuses what it cannot
/// read rather than guessing: indexing the bytes of a binary as though they were text would put a document into the
/// index that no query can find and no citation can justify. The refusal names the media type, and a caller that
/// wants a richer set of formats brings an extractor of its own - the seam is a media type in and text out.
/// <para>
/// Media types arrive with parameters, so the type is read up to the first <c>;</c> and compared without case: a
/// caller sending <c>text/plain; charset=utf-8</c> means the same thing as one sending <c>text/plain</c>, and a
/// comparison that treated them as different formats would refuse documents for their punctuation.
/// </para>
/// </remarks>
public static class TextExtractor
{
    /// <summary>The textual media types that are not <c>text/*</c>.</summary>
    private static readonly string[] TextualApplications = ["application/json", "application/xml"];

    private static readonly string[] TextualSuffixes = ["+json", "+xml"];

    /// <summary>
    /// Reports whether a media type can be read as text.
    /// </summary>
    /// <param name="mediaType">The media type, with or without parameters.</param>
    /// <returns><see langword="true"/> when <see cref="Extract"/> would answer rather than refuse.</returns>
    public static bool CanExtract(string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        var type = Normalize(mediaType);

        return type.StartsWith("text/", StringComparison.Ordinal)
            || TextualApplications.Contains(type, StringComparer.Ordinal)
            || TextualSuffixes.Any(suffix => type.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads a document's text.
    /// </summary>
    /// <param name="mediaType">The media type, with or without parameters.</param>
    /// <param name="bytes">The document's bytes.</param>
    /// <returns>The text, with a byte-order mark dropped.</returns>
    /// <exception cref="ArgumentException">Thrown when no extractor reads that media type.</exception>
    public static string Extract(string mediaType, ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        var type = Normalize(mediaType);

        if (!CanExtract(type))
        {
            throw new ArgumentException(
                $"no extractor for media type '{type}' is ported; this port reads text/{'*'}, "
                    + "application/json and application/xml, and the upstream extractors for PDF and DOCX depend on "
                    + "a model capability it has not ported",
                nameof(mediaType));
        }

        // UTF-8, and a leading byte-order mark dropped: a mark left in place would begin the first chunk with a
        // zero-width character that the analyzer would then treat as part of the first term.
        var text = System.Text.Encoding.UTF8.GetString(bytes.Span);

        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    private static string Normalize(string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        var separator = mediaType.IndexOf(';', StringComparison.Ordinal);
        var type = separator < 0 ? mediaType : mediaType[..separator];

        return type.Trim().ToLowerInvariant();
    }
}
