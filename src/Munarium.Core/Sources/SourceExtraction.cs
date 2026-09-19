namespace Munarium.Sources;

using Munarium.Text;

/// <summary>Turns a source's bytes into text: locally first, and then the provider, if local found nothing usable.</summary>
/// <remarks>
/// The order is the cost control and the whole point of the seam. Local extractors are free and run on everything; the
/// provider is reached only for a document that produced no text at all - a scan, or a PDF whose page images use an
/// encoding no decoder here handles.
/// <para>
/// A provider that answers sets the outcome to <see cref="ExtractionStatus.Ok"/> with
/// <see cref="ExtractionMethod.Ocr"/>, because that is what happened: a model read the pages. A provider that answers
/// with nothing leaves the local outcome standing - that is a truer <c>empty</c> than the local one, and it is still the
/// same fact. A provider that fails leaves it standing too: an outage at the vendor degrades the index rather than
/// failing a build, and the row records the local outcome it would have had anyway.
/// </para>
/// <para>
/// What this port does not carry is the original's log line naming the provider and its fingerprint. There is no logging
/// seam in the kernel, and inventing one to record a fact the source row cannot hold would be worse than saying so: the
/// row records that OCR produced the text, not which model did.
/// </para>
/// </remarks>
/// <param name="provider">The escalation path, or <see langword="null"/> when no provider is configured.</param>
public sealed class SourceExtraction(IDocumentIntelligence? provider = null)
{
    /// <summary>Gets the provider, which is always optional.</summary>
    public IDocumentIntelligence? Provider { get; } = provider;

    /// <summary>Extracts a source's text.</summary>
    /// <param name="mediaType">The media type, with or without parameters.</param>
    /// <param name="bytes">The document's bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text and how it was obtained.</returns>
    /// <exception cref="ArgumentException">Thrown when no extractor reads that media type.</exception>
    public async ValueTask<Extracted> ExtractAsync(
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        var local = TextExtractor.Extract(mediaType, bytes);

        // Only a document nothing came out of is worth paying for. A failure is not escalated: a DOCX that is not a zip
        // is not a scan, and a provider cannot recover what was never a readable document.
        if (local.Status is not ExtractionStatus.Empty || Provider is null || !Provider.Supports(mediaType))
        {
            return local;
        }

        try
        {
            var analyzed = await Provider
                .AnalyzeAsync(mediaType, bytes, cancellationToken)
                .ConfigureAwait(false);

            return analyzed.IsEmpty
                ? local
                : new Extracted(analyzed.Text, ExtractionStatus.Ok, ExtractionMethod.Ocr);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or TimeoutException
            or InvalidOperationException or TaskCanceledException)
        {
            return local;
        }
    }
}
