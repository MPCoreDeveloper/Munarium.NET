namespace Munarium.Sources;

/// <summary>What a document-intelligence provider returned for one document.</summary>
/// <param name="Text">The extracted text, page blocks separated by blank lines so the chunker sees page boundaries.</param>
/// <param name="PagesAnalyzed">Pages actually analyzed, which is the unit most services bill on.</param>
/// <param name="ProviderFingerprint">
/// Provider, model and API version - for example <c>azure-docintel/prebuilt-read/2024-11-30</c> - and never a secret.
/// A hosted model is reproducible only against a pinned version, which is why a provider states one.
/// </param>
public sealed record AnalyzedDocument(string Text, int PagesAnalyzed, string ProviderFingerprint)
{
    /// <summary>Builds the answer for a document a service read and found nothing in.</summary>
    /// <remarks>
    /// An outcome and not an error, deliberately: "nothing there" and "the call failed" lead to different operator
    /// actions, and collapsing them hides real gaps.
    /// </remarks>
    /// <param name="providerFingerprint">The provider's fingerprint.</param>
    /// <returns>The answer.</returns>
    public static AnalyzedDocument Empty(string providerFingerprint) =>
        new(string.Empty, 0, providerFingerprint);

    /// <summary>Gets a value indicating whether the service returned no usable text.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// A hosted or on-premises document analyzer: the escalation path for a document local extraction could not read.
/// </summary>
/// <remarks>
/// Separate from <see cref="Munarium.Text.TextExtractor"/> on purpose, and the differences are the whole reason. Local extraction is
/// free, synchronous and reproducible from bytes forever. A document-intelligence service is none of those: it is a
/// network call, it costs money per page, and its output can change when the vendor updates a model. So:
/// <list type="bullet">
/// <item><description>
/// Cost is a deliberate choice. A local extractor runs on every source in a build; a per-page billed service cannot, so
/// this is reached only when local extraction produced nothing at all.
/// </description></item>
/// <item><description>
/// It is optional. The system runs, and every test passes, with no provider configured - which is exactly how this port
/// ships, because its providers live outside the kernel.
/// </description></item>
/// <item><description>
/// It is where OCR lives. This port has no local OCR engine - no pure-managed one exists for .NET, and a native one
/// would contradict the AOT job - so a scan is read here or not at all, and a provider that answers sets the source's
/// method to <c>ocr</c>.
/// </description></item>
/// </list>
/// <para>
/// A conforming provider must: report <see cref="Supports"/> honestly, because the escalation skips what you decline and a
/// false yes becomes a billed no-op; return <see cref="AnalyzedDocument.Empty"/> rather than an error when the service
/// genuinely found no text; bound itself with a page cap and a wall-clock timeout; and carry no credentials in its
/// fingerprint, which is recorded.
/// </para>
/// </remarks>
public interface IDocumentIntelligence
{
    /// <summary>Gets a stable identifier for logs, metrics and the extraction record.</summary>
    string Id { get; }

    /// <summary>Reports whether this provider will attempt a media type.</summary>
    /// <param name="mediaType">The media type, with or without parameters.</param>
    /// <returns><see langword="true"/> when the provider wants the document.</returns>
    bool Supports(string mediaType);

    /// <summary>Analyzes one document.</summary>
    /// <remarks>
    /// Implementations bound their own page count and wall-clock time. A failure may throw: the caller keeps the local
    /// result and continues, so an outage at the vendor degrades the index rather than failing a build.
    /// </remarks>
    /// <param name="mediaType">The media type, with or without parameters.</param>
    /// <param name="bytes">The document's bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the service found.</returns>
    ValueTask<AnalyzedDocument> AnalyzeAsync(
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);
}
