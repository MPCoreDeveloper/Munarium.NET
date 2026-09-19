namespace Munarium.Sources;

/// <summary>
/// What a deployment knows about one source document: which path, which bytes, and where they went.
/// </summary>
/// <remarks>
/// The bytes and this row are separate on purpose: <see cref="ISourceStore"/> moves documents and knows nothing about
/// the ledger, while the row is what an index build reads to decide what to index and what an operator reads to
/// answer "which file is that chunk from".
/// <para>
/// <see cref="SourceId"/> is derived from tenant and path rather than from the content, so re-ingesting one path with
/// new bytes keeps the source and changes its hash. That is the case the index identity is built to notice: it pairs
/// the source with its hash, so new bytes at one path rebuild rather than quietly serving the old text.
/// </para>
/// </remarks>
public sealed record SourceRecord
{
    /// <summary>Gets the tenant the path belongs to.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the source's identity, derived from the tenant and the path.</summary>
    public required string SourceId { get; init; }

    /// <summary>Gets the logical path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the hash of the bytes this path holds, in the canonical form.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Gets the media type the bytes were stored as.</summary>
    public required string MediaType { get; init; }

    /// <summary>Gets how many bytes that is.</summary>
    public required long BytesLength { get; init; }

    /// <summary>Gets the backend-resolved location, so an operator can be told where the bytes went.</summary>
    public required string BlobUri { get; init; }

    /// <summary>Gets the backend that holds them.</summary>
    public required string BackendId { get; init; }

    /// <summary>
    /// Gets when the row was last written, as the backend recorded it, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A server-owned lifecycle fact, stamped by whoever has a clock - which is not the kernel: a verdict or an
    /// index identity that depended on a clock read could not be rebuilt later.
    /// </remarks>
    public string? IngestedAt { get; init; }

    /// <summary>
    /// Gets how the row's extraction went — <c>ok</c>, <c>empty</c> or <c>failed</c> — or <see langword="null"/> when the
    /// document has not been extracted yet.
    /// </summary>
    /// <remarks>
    /// The original's "invisible-document signal": a document that yields nothing has to be visible in the data rather
    /// than look like one nobody ingested. A re-upload clears both fields, because the new bytes have not been read yet,
    /// and they are stored as names rather than ordinals so a row is readable in the table.
    /// </remarks>
    public string? ExtractionStatus { get; init; }

    /// <summary>
    /// Gets how the text was obtained — <c>text</c>, <c>docx</c>, <c>pdf-text</c> or <c>ocr</c> — or
    /// <see langword="null"/> when the document has not been extracted yet.
    /// </summary>
    /// <remarks>
    /// Recorded because an OCR'd document and a text-layer one are not equivalent evidence, and because a scan and a
    /// text document whose extractor yielded nothing are different facts: the status says nothing came out, this says
    /// which extractor found nothing.
    /// </remarks>
    public string? ExtractionMethod { get; init; }
}
