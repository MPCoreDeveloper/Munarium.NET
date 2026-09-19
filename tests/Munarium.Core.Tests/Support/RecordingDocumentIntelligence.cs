namespace Munarium.Core.Tests.Support;

using Munarium.Sources;

/// <summary>
/// A document-intelligence provider that records what it was asked, so a test can prove the escalation reaches it only
/// when local extraction found nothing.
/// </summary>
/// <remarks>
/// Its default answer is the one a real OCR service gives for a blank page: <see cref="AnalyzedDocument.Empty"/>, which is
/// an answer and not a failure. Setting <see cref="Recovered"/> makes it answer with text, and setting
/// <see cref="Failing"/> makes it throw, which is the case the caller must survive.
/// </remarks>
internal sealed class RecordingDocumentIntelligence : IDocumentIntelligence
{
    /// <summary>Gets or sets the text the provider answers with.</summary>
    public string? Recovered { get; set; }

    /// <summary>Gets or sets a value indicating whether the provider throws instead of answering.</summary>
    public bool Failing { get; set; }

    /// <summary>Gets or sets the media types the provider declines, which is how a false yes is avoided.</summary>
    public bool SupportsEverything { get; set; } = true;

    /// <summary>Gets the media types the provider was asked about.</summary>
    public List<string> Asked { get; } = [];

    /// <inheritdoc />
    public string Id => "recording";

    /// <inheritdoc />
    public bool Supports(string mediaType) => SupportsEverything;

    /// <inheritdoc />
    public ValueTask<AnalyzedDocument> AnalyzeAsync(
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        Asked.Add(mediaType);

        if (Failing)
        {
            throw new HttpRequestException("the service is down, which must not fail a build");
        }

        return ValueTask.FromResult(
            Recovered is { Length: > 0 } text
                ? new AnalyzedDocument(text, PagesAnalyzed: 3, "recording/scan@1")
                : AnalyzedDocument.Empty("recording/scan@1"));
    }
}
