// NativeAOT smoke test for Munarium.NET.
//
// It exercises the ledger seam end to end inside a fully AOT-compiled binary: append, refuse a
// stale write, read the head. If any of it needs runtime code generation or reflection, the AOT
// publish would have failed before this ever ran.

using Munarium.Commands;
using Munarium.Governance;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Shapes;
using Munarium.Store.SharpCoreDb;
using Munarium.Text;
using SharpCoreDB.EventSourcing;

var backend = new SharpCoreDbStorageBackend(new InMemoryEventStore());
var stream = StreamId.From("aot/claim");

var first = await backend.AppendAsync(stream, SequenceNumber.Zero, [LedgerEvent.FromText("claim.recorded", "north")]);
if (Describe(first) != "appended:1")
{
    await Console.Error.WriteLineAsync($"FAIL: expected appended:1, got {Describe(first)}");
    return 1;
}

var stale = await backend.AppendAsync(stream, SequenceNumber.Zero, [LedgerEvent.FromText("claim.recorded", "south")]);
if (Describe(stale) != "conflict:0->1")
{
    await Console.Error.WriteLineAsync($"FAIL: expected conflict:0->1, got {Describe(stale)}");
    return 1;
}

var head = await backend.HeadAsync(stream);
if (head != new SequenceNumber(1))
{
    await Console.Error.WriteLineAsync($"FAIL: expected head 1, got {head}");
    return 1;
}

// The retrieval path too: the index host and SharpCoreDB's vector index have to survive AOT as well.
var embedder = new DeterministicEmbeddingProvider(64);
using var host = new SharpCoreDbIndexHost(64, "aot-index@1", head);
host.ServingWriter.Index(
    new SourceReference("chunk-1", "source-1", "docs/policy.pdf", "sha256:policy", ChunkOrdinal: 0),
    "the supplier is north",
    embedder.Embed("the supplier is north"));

var retrieval = await host.ServingReader.SearchAsync(new RetrievalQuery
{
    Text = "supplier north",
    Embedding = embedder.Embed("supplier north"),
    TopK = 1,
});

if (retrieval.Chunks.Count != 1 || retrieval.Envelope.Sources.Count != 1)
{
    await Console.Error.WriteLineAsync(
        $"FAIL: retrieval returned {retrieval.Chunks.Count} chunks / {retrieval.Envelope.Sources.Count} sources");
    return 1;
}

// The shape path: schema validation and lineage derivation have to survive AOT as well.
var shapes = new ShapeRegistry([
    new FactShape
    {
        Name = "vendor",
        Version = 1,
        Identity = ["vendor_id"],
        Schema = """
            {
              "type": "object",
              "required": ["vendor_id"],
              "properties": { "vendor_id": { "type": "string", "pattern": "^v-[0-9]+$" } }
            }
            """,
    },
]);

var claims = new ClaimLedger(backend, shapes, [new ShapeGate(shapes)]);
var asserted = DescribeClaim(await claims.RecordAsync(Claim("v-1")));
var refused = DescribeClaim(await claims.RecordAsync(Claim("north")));

if (asserted != "asserted" || refused != "disputed:shape")
{
    await Console.Error.WriteLineAsync($"FAIL: expected asserted/disputed:shape, got {asserted}/{refused}");
    return 1;
}

// The document path too: a PDF's text layer is read through PdfPig, which is pure managed but is still
// a parser with fonts, filters and encodings inside it - exactly the kind of dependency that can need
// runtime codegen. If it does, this publish or this read fails.
const string PdfWithTextLayer = """
    %PDF-1.4
    1 0 obj
    << /Type /Catalog /Pages 2 0 R >>
    endobj
    2 0 obj
    << /Type /Pages /Kids [3 0 R] /Count 1 >>
    endobj
    3 0 obj
    << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>
    endobj
    4 0 obj
    << /Length 80 >>
    stream
    BT /F1 12 Tf 72 720 Td (The quarterly settlement was approved on 14 March) Tj ET
    endstream
    endobj
    5 0 obj
    << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
    endobj
    trailer
    << /Size 6 /Root 1 0 R >>
    %%EOF
    """;

var pdfText = TextExtractor.Extract("application/pdf", System.Text.Encoding.ASCII.GetBytes(PdfWithTextLayer)).Text;

if (!pdfText.Contains("quarterly settlement", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync($"FAIL: pdf extraction returned '{pdfText}'");
    return 1;
}

await Console.Out.WriteLineAsync(
    $"Munarium NativeAOT smoke OK ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}) "
    + $"- head={head}, retrieval={retrieval.Envelope.Sources[0].SourcePath} @ {retrieval.Envelope.IndexVersion}, "
    + $"shapes={shapes.Count} ({asserted}, {refused}), pdf={pdfText.Length} chars");
return 0;

static RecordClaimCommand Claim(string vendorId) => new()
{
    VersionId = "aot/shape",
    ClaimId = $"claim-{vendorId}",
    Shape = "vendor",
    Body = $$"""{"vendor_id":"{{vendorId}}"}""",
    Statement = "the supplier is north",
    Actor = "aot",
};

static string DescribeClaim(ClaimOutcome outcome) => outcome switch
{
    ClaimAsserted => "asserted",
    ClaimRecordedAsDisputed disputed => $"disputed:{disputed.Gate}",
    ClaimContended contended => $"contended:{contended.Expected.Value}->{contended.Actual.Value}",
};

static string Describe(AppendOutcome outcome) => outcome switch
{
    Appended appended => $"appended:{appended.Head.Value}",
    VersionConflict conflict => $"conflict:{conflict.Expected.Value}->{conflict.Actual.Value}",
};
