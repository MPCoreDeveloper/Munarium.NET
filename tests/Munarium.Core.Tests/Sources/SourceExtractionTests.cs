namespace Munarium.Core.Tests.Sources;

using Munarium.Core.Tests.Support;
using Munarium.Sources;
using Munarium.Text;

/// <summary>
/// Tests for the escalation path: local extraction first, and a document-intelligence provider only when that found
/// nothing - never for a document that was read, and never in a way that a provider outage can fail a build.
/// </summary>
public class SourceExtractionTests
{
    private const string Media = "application/pdf";

    /// <summary>A PDF with no text layer is a scan, and a scan is exactly what the provider is for.</summary>
    [Fact]
    public async Task AScanIsEscalatedAndComesBackAsOcr()
    {
        var provider = new RecordingDocumentIntelligence { Recovered = "The quarterly settlement was approved." };
        var extraction = new SourceExtraction(provider);

        var extracted = await extraction.ExtractAsync(Media, Scan());

        Assert.Equal(ExtractionStatus.Ok, extracted.Status);
        // The method says a model read the pages, which is what makes an OCR'd document distinguishable in the row.
        Assert.Equal(ExtractionMethod.Ocr, extracted.Method);
        Assert.Equal("The quarterly settlement was approved.", extracted.Text);
        Assert.Equal([Media], provider.Asked);
    }

    /// <summary>
    /// A document local extraction read is never sent to a billed service, which is the whole cost control.
    /// </summary>
    [Fact]
    public async Task ADocumentThatWasReadLocallyIsNotEscalated()
    {
        var provider = new RecordingDocumentIntelligence { Recovered = "never used" };
        var extraction = new SourceExtraction(provider);

        var extracted = await extraction.ExtractAsync("text/plain", "The Bell rang twice."u8.ToArray());

        Assert.Equal(ExtractionStatus.Ok, extracted.Status);
        Assert.Equal(ExtractionMethod.Text, extracted.Method);
        Assert.Empty(provider.Asked);
    }

    /// <summary>A provider that declines a media type is skipped rather than paid for a guaranteed empty result.</summary>
    [Fact]
    public async Task ADeclinedMediaTypeIsNotSent()
    {
        var provider = new RecordingDocumentIntelligence { SupportsEverything = false };
        var extraction = new SourceExtraction(provider);

        var extracted = await extraction.ExtractAsync(Media, Scan());

        Assert.Equal(ExtractionStatus.Empty, extracted.Status);
        Assert.Equal(ExtractionMethod.PdfTextLayer, extracted.Method);
        Assert.Empty(provider.Asked);
    }

    /// <summary>
    /// A provider that fails leaves the local outcome standing: an outage degrades the index rather than failing a build.
    /// </summary>
    [Fact]
    public async Task AFailingProviderLeavesTheLocalOutcome()
    {
        var provider = new RecordingDocumentIntelligence { Failing = true };
        var extraction = new SourceExtraction(provider);

        var extracted = await extraction.ExtractAsync(Media, Scan());

        Assert.Equal(ExtractionStatus.Empty, extracted.Status);
        Assert.Equal(ExtractionMethod.PdfTextLayer, extracted.Method);
        Assert.Equal([Media], provider.Asked);
    }

    /// <summary>A provider that read the document and found nothing changes nothing: it is the same empty.</summary>
    [Fact]
    public async Task AProviderThatFoundNothingLeavesTheLocalOutcome()
    {
        var provider = new RecordingDocumentIntelligence();
        var extraction = new SourceExtraction(provider);

        var extracted = await extraction.ExtractAsync(Media, Scan());

        Assert.Equal(ExtractionStatus.Empty, extracted.Status);
        Assert.Equal(ExtractionMethod.PdfTextLayer, extracted.Method);
    }

    /// <summary>With no provider configured the escalation does not exist, which is how this port ships.</summary>
    [Fact]
    public async Task NoProviderMeansNoEscalation()
    {
        var extracted = await new SourceExtraction().ExtractAsync(Media, Scan());

        Assert.Equal(ExtractionStatus.Empty, extracted.Status);
        Assert.Equal(ExtractionMethod.PdfTextLayer, extracted.Method);
    }

    /// <summary>Builds a PDF whose page carries no text layer, which is what a scan is.</summary>
    /// <returns>The bytes.</returns>
    private static byte[] Scan()
    {
        const string Content = "q 612 0 0 792 0 0 cm /Im0 Do Q";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 4 0 R >> >> "
                + "/Contents 5 0 R >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray "
                + "/BitsPerComponent 8 /Length 1 >>\nstream\n\u0000\nendstream",
            $"<< /Length {Content.Length} >>\nstream\n{Content}\nendstream",
        ];

        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
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

        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
