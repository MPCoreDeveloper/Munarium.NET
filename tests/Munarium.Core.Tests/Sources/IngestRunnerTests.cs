namespace Munarium.Core.Tests.Sources;

using Munarium.Core.Tests.Support;
using Munarium.Evidence;
using Munarium.Ledger;
using Munarium.Sources;

/// <summary>
/// Tests for the ingest path end to end: the bytes, the row, the chunks and the index - and the states in between,
/// because "stored but not indexed" is a state a caller has to be able to see.
/// </summary>
public class IngestRunnerTests
{
    /// <summary>
    /// A document goes in and comes back out of retrieval, with the citation resolving to the path and hash it was
    /// stored under. This is the whole chain in one test: source, row, chunks, index, envelope, watermark.
    /// </summary>
    /// <remarks>
    /// The ingest writes into the serving writer and answers with that writer's own version, so the chunks and the
    /// answer cannot disagree - even if a cutover happened while the ingest was running.
    /// </remarks>
    [Fact]
    public async Task AnIngestLandsInTheVersionThatServesNow()
    {
        var fixture = Fixture();
        var second = fixture.Host.Build("idx-second", new SequenceNumber(7));
        fixture.Host.Serve("idx-second");

        var ingested = Ingested(
            await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", Bytes(Document)));

        Assert.Equal("idx-second", ingested.IndexVersion);
        Assert.Equal(0, fixture.Index.Count);
        Assert.Equal(ingested.ChunksIndexed, second.Writer.Count);
    }

    private const string Version = "idx-test";

    private const string Document =
        "The Bell rang twice.\n\n" +
        "A second paragraph, longer than the first, with a sentence in it.\n\n" +
        "And a closing paragraph.";

    /// <summary>
    /// A new document reaches the index with citations that resolve: the chunk id names the source and the ordinal, and
    /// the path and the hash travel with it, because an answer has to be able to say which document answered.
    /// </summary>
    [Fact]
    public async Task ANewDocumentIsStoredChunkedEmbeddedAndIndexed()
    {
        var fixture = Fixture();
        var bytes = Bytes(Document);

        var ingested = Ingested(await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", bytes));

        Assert.Equal(SourceIngestKind.New, ingested.Kind);
        Assert.Equal("idx-test", ingested.IndexVersion);
        Assert.True(ingested.ChunksIndexed > 1, "the document should have needed more than one chunk");
        Assert.Equal(ingested.ChunksIndexed, fixture.Index.Count);
        Assert.Single(fixture.Provider.Requests);

        var row = ingested.Record;
        var first = fixture.Index.Chunks[0];

        Assert.Equal(string.Concat(row.SourceId, "#0"), first.Source.ChunkId);
        Assert.Equal(row.SourceId, first.Source.SourceId);
        Assert.Equal("docs/note.txt", first.Source.SourcePath);
        Assert.Equal(ArtifactContent.Hash(bytes), first.Source.ContentHash);
        Assert.Equal(Enumerable.Range(0, ingested.ChunksIndexed), fixture.Index.Chunks.Select(c => c.Source.ChunkOrdinal));
        Assert.Equal(
            fixture.Index.Chunks.Select(chunk => chunk.Text),
            fixture.Provider.Requests[0].Inputs);
    }

    /// <summary>
    /// A vector belongs to the text it was computed from. The recording provider marks each vector with its input's
    /// ordinal, so a mis-pairing shows up here rather than as a query that returns the wrong document.
    /// </summary>
    [Fact]
    public async Task EveryChunkGetsTheVectorComputedForIt()
    {
        var fixture = Fixture();

        var ingested = Ingested(await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", Bytes(Document)));

        for (var ordinal = 0; ordinal < ingested.ChunksIndexed; ordinal++)
        {
            Assert.All(
                fixture.Index.Chunks[ordinal].Embedding,
                value => Assert.Equal(ordinal + 1, value));
        }
    }

    /// <summary>
    /// A provider that answers with the wrong number of vectors would have them attached to the wrong text, so the
    /// ingest fails loudly and writes nothing into the index - while the row stays, which is the state a rebuild reads.
    /// </summary>
    [Fact]
    public async Task AProviderThatAnswersWithTheWrongVectorCountIndexesNothing()
    {
        var fixture = Fixture(vectorCount: 1);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", Bytes(Document)).AsTask());

        Assert.Contains("cannot be paired", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Index.Count);
        Assert.Equal(1, fixture.Registry.Count);
        Assert.Equal(1, fixture.Store.Count);
    }

    /// <summary>
    /// A re-put of the same bytes is not a second ingest: nothing is written, nothing is embedded, and nothing is
    /// indexed twice, because a document indexed twice would answer twice and displace other documents by existing.
    /// </summary>
    [Fact]
    public async Task ARePutOfTheSameBytesIsNotIndexedAgain()
    {
        var fixture = Fixture();
        var bytes = Bytes(Document);
        var first = Ingested(await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", bytes));

        var second = Ingested(await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", bytes));

        Assert.Equal(SourceIngestKind.Unchanged, second.Kind);
        Assert.Equal(0, second.ChunksIndexed);
        Assert.Equal(first.Record, second.Record);
        Assert.Equal(first.ChunksIndexed, fixture.Index.Count);
        Assert.Single(fixture.Provider.Requests);
    }

    /// <summary>
    /// The row records how extraction went, which is what makes a document that contributed nothing visible.
    /// </summary>
    /// <remarks>
    /// The original calls these two fields the invisible-document signal and writes them at index time. This port
    /// indexes as it ingests, so that is where they are written - and the answer carries the row as it now stands, so
    /// a caller sees what a later read would.
    /// </remarks>
    [Fact]
    public async Task TheRowRecordsHowExtractionWent()
    {
        var fixture = Fixture();

        var ingested = Ingested(await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", Bytes(Document)));

        Assert.Equal("ok", ingested.Record.ExtractionStatus);
        Assert.Equal("text", ingested.Record.ExtractionMethod);

        var stored = await fixture.Registry.FindAsync("acme", "docs/note.txt");

        Assert.Equal("ok", stored?.ExtractionStatus);
        Assert.Equal("text", stored?.ExtractionMethod);
    }

    /// <summary>
    /// A document that is not what it claims is recorded as a failed extraction, and the ingest still answers: the
    /// bytes are real, and refusing them would lose a document an operator may want to look at.
    /// </summary>
    [Fact]
    public async Task ADocumentThatIsNotWhatItClaimsIsRecordedAsFailed()
    {
        var fixture = Fixture();

        var ingested = Ingested(
            await fixture.Runner.IngestAsync(
                "acme",
                "docs/scan.pdf",
                "application/pdf",
                System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 not really a pdf")));

        Assert.Equal(0, ingested.ChunksIndexed);
        Assert.Equal("failed", ingested.Record.ExtractionStatus);
        Assert.Equal("pdf-text", ingested.Record.ExtractionMethod);
    }

    public async Task AChangedDocumentAtTheSamePathReplacesTheSourceAndIsIndexed()
    {
        var fixture = Fixture();
        await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", Bytes(Document));

        var ingested = Ingested(
            await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", Bytes("The Bell rang three times.")));

        Assert.Equal(SourceIngestKind.Replaced, ingested.Kind);
        Assert.Equal(1, ingested.ChunksIndexed);
        Assert.Equal(1, fixture.Registry.Count);
        Assert.Equal(2, fixture.Provider.Requests.Count);
    }

    /// <summary>
    /// A media type this port cannot read is refused before anything happens: no bytes, no row, no embedding call.
    /// Storing a document nobody can read would leave a source that can never be retrieved and a row that promises
    /// otherwise.
    /// </summary>
    [Fact]
    public async Task AMediaTypeWithNoExtractorIsRefusedBeforeAnythingIsWritten()
    {
        var fixture = Fixture();

        var refused = Refused(
            await fixture.Runner
                .IngestAsync("acme", "docs/plate.tiff", "image/tiff", new byte[] { 0x49, 0x49, 0x2A, 0x00 }));

        Assert.Contains("image/tiff", refused.Reason, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Store.Count);
        Assert.Equal(0, fixture.Registry.Count);
        Assert.Equal(0, fixture.Index.Count);
        Assert.Empty(fixture.Provider.Requests);
    }

    [Fact]
    public async Task ADeclaredHashMismatchIsRefusedAndNothingIsIndexed()
    {
        var fixture = Fixture();

        var rejected = Rejected(
            await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", Bytes(Document), "sha256:0000"));

        Assert.Equal(ArtifactContent.Hash(Bytes(Document)), rejected.Actual);
        Assert.Equal(0, fixture.Store.Count);
        Assert.Equal(0, fixture.Index.Count);
        Assert.Empty(fixture.Provider.Requests);
    }

    /// <summary>
    /// A document of nothing but whitespace is stored and recorded - the bytes are real - but there is nothing to
    /// index and no reason to spend an embedding call finding that out.
    /// </summary>
    [Fact]
    public async Task AWhitespaceOnlyDocumentIsStoredWithNothingIndexed()
    {
        var fixture = Fixture();

        var ingested = Ingested(
            await fixture.Runner.IngestAsync("acme", "docs/blank.txt", "text/plain", Bytes("   \n\n\t ")));

        Assert.Equal(SourceIngestKind.New, ingested.Kind);
        Assert.Equal(0, ingested.ChunksIndexed);
        Assert.Equal(1, fixture.Registry.Count);
        Assert.Empty(fixture.Provider.Requests);
    }

    /// <summary>The ceiling is a property of the index rather than a suggestion: nothing longer is ever written.</summary>
    [Fact]
    public async Task NoIndexedChunkExceedsTheCeiling()
    {
        var fixture = Fixture(maxChunkChars: 40);

        var ingested = Ingested(await fixture.Runner.IngestAsync("acme", "docs/note.txt", "text/plain", Bytes(Document)));

        Assert.True(ingested.ChunksIndexed >= 2);
        Assert.All(fixture.Index.Chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 40));
    }

    [Fact]
    public void AnIngestWithoutAModelOrACeilingIsRefused()
    {
        var registry = new InMemorySourceRegistry();
        var ingest = new SourceIngest(new InMemorySourceStore(), registry);
        var provider = new RecordingEmbeddingProvider();
        var host = new FakeIndexHost();

        Assert.Throws<ArgumentException>(() => new IngestRunner(ingest, provider, host, model: " ", registry: registry));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IngestRunner(ingest, provider, host, model: "m", registry: registry, maxChunkChars: 0));
    }

    private static (IngestRunner Runner,
        InMemorySourceStore Store,
        InMemorySourceRegistry Registry,
        RecordingEmbeddingProvider Provider,
        RecordingIndexWriter Index,
        FakeIndexHost Host) Fixture(int? vectorCount = null, int maxChunkChars = 30)
    {
        var store = new InMemorySourceStore();
        var registry = new InMemorySourceRegistry();
        var provider = new RecordingEmbeddingProvider(vectorCount: vectorCount);
        var host = new FakeIndexHost();

        // A deployment starts by serving something: the ingest's own test double builds one version and serves it, so
        // what the runner answers matches what the host would say.
        host.Build(Version, SequenceNumber.Zero);
        host.Serve(Version);

        return (
            new IngestRunner(new SourceIngest(store, registry), provider, host, "test-model", registry, maxChunkChars),
            store,
            registry,
            provider,
            (RecordingIndexWriter)host.Instances[Version].Writer,
            host);
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static IngestedDocument Ingested(DocumentOutcome outcome) =>
        outcome is IngestedDocument ingested ? ingested : throw new InvalidOperationException("Nothing was ingested.");

    private static IngestRefused Refused(DocumentOutcome outcome) =>
        outcome is IngestRefused refused ? refused : throw new InvalidOperationException("Nothing was refused.");

    private static IngestRejected Rejected(DocumentOutcome outcome) =>
        outcome is IngestRejected rejected ? rejected : throw new InvalidOperationException("Nothing was rejected.");
}
