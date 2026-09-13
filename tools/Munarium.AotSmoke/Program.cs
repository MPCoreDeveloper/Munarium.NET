// NativeAOT smoke test for Munarium.NET.
//
// It exercises the ledger seam end to end inside a fully AOT-compiled binary: append, refuse a
// stale write, read the head. If any of it needs runtime code generation or reflection, the AOT
// publish would have failed before this ever ran.

using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Store.SharpCoreDb;
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

// The retrieval path too: SharpCoreDB's vector index has to survive AOT as well.
var embedder = new DeterministicEmbeddingProvider(64);
using var retriever = new SharpCoreDbRetriever(64, "aot-index@1", head);
retriever.Index(
    new SourceReference("chunk-1", "source-1", "docs/policy.pdf", "sha256:policy", ChunkOrdinal: 0),
    "the supplier is north",
    embedder.Embed("the supplier is north"));

var retrieval = await retriever.SearchAsync(new RetrievalQuery
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

await Console.Out.WriteLineAsync(
    $"Munarium NativeAOT smoke OK ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}) "
    + $"- head={head}, retrieval={retrieval.Envelope.Sources[0].SourcePath} @ {retrieval.Envelope.IndexVersion}");
return 0;

static string Describe(AppendOutcome outcome) => outcome switch
{
    Appended appended => $"appended:{appended.Head.Value}",
    VersionConflict conflict => $"conflict:{conflict.Expected.Value}->{conflict.Actual.Value}",
};
