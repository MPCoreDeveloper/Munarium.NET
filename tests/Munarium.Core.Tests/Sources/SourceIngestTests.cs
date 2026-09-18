namespace Munarium.Core.Tests.Sources;

using Munarium.Core.Tests.Support;
using Munarium.Evidence;
using Munarium.Sources;

/// <summary>
/// Tests for the ingest path: the path is checked before anything is touched, a declared hash is verified before the
/// write, and a document that is already there is not written again.
/// </summary>
public class SourceIngestTests
{
    private static readonly byte[] Bytes = "The Bell rang twice."u8.ToArray();

    [Fact]
    public async Task ANewDocumentIsStoredAndRecorded()
    {
        var (ingest, store, registry) = Fixture();

        var outcome = Ingested(await ingest.PutAsync("acme", "docs/note.txt", "text/plain", Bytes));

        Assert.Equal(SourceIngestKind.New, outcome.Kind);
        Assert.Equal(SourceKey.Id("acme", "docs/note.txt"), outcome.Record.SourceId);
        Assert.Equal(ArtifactContent.Hash(Bytes), outcome.Record.ContentHash);
        Assert.Equal(Bytes.LongLength, outcome.Record.BytesLength);
        Assert.Equal("mem", outcome.Record.BackendId);
        Assert.Equal("mem://acme/docs/note.txt", outcome.Record.BlobUri);
        Assert.Equal("stamp-1", outcome.Record.IngestedAt);
        Assert.Equal(1, store.Count);
        Assert.Equal(1, registry.Count);
        Assert.Equal(Bytes, await store.GetAsync(SourceKey.New("acme", "docs/note.txt", outcome.Record.ContentHash)));
    }

    /// <summary>
    /// A command that changed nothing must not look like one that did: identical bytes are not written again, and the
    /// row that answers is the one already there.
    /// </summary>
    [Fact]
    public async Task BytesThatAreAlreadyThereAreNotWrittenAgain()
    {
        var (ingest, store, registry) = Fixture();
        var first = Ingested(await ingest.PutAsync("acme", "docs/note.txt", "text/plain", Bytes));

        var second = Ingested(await ingest.PutAsync("acme", "docs/note.txt", "text/plain", Bytes));

        Assert.Equal(SourceIngestKind.Unchanged, second.Kind);
        Assert.Equal(first.Record, second.Record);
        Assert.Equal(1, store.Count);
        Assert.Equal(1, registry.Count);
    }

    /// <summary>
    /// The same path with different bytes is one source replaced, not a second source: the identity is the path, so
    /// the row is upserted and the hash moves - which is what an index build notices.
    /// </summary>
    [Fact]
    public async Task TheSamePathWithDifferentBytesReplacesTheSource()
    {
        var (ingest, store, registry) = Fixture();
        var first = Ingested(await ingest.PutAsync("acme", "docs/note.txt", "text/plain", Bytes));
        var changed = "The Bell rang three times."u8.ToArray();

        var second = Ingested(await ingest.PutAsync("acme", "docs/note.txt", "text/plain", changed));

        Assert.Equal(SourceIngestKind.Replaced, second.Kind);
        Assert.Equal(first.Record.SourceId, second.Record.SourceId);
        Assert.NotEqual(first.Record.ContentHash, second.Record.ContentHash);
        Assert.Equal(1, registry.Count);
        Assert.Equal(changed, await store.GetAsync(SourceKey.New("acme", "docs/note.txt", second.Record.ContentHash)));
    }

    [Fact]
    public async Task TheSameBytesAtTwoPathsAreTwoSources()
    {
        var (ingest, store, _) = Fixture();

        var one = Ingested(await ingest.PutAsync("acme", "docs/a.txt", "text/plain", Bytes));
        var two = Ingested(await ingest.PutAsync("acme", "docs/b.txt", "text/plain", Bytes));

        Assert.NotEqual(one.Record.SourceId, two.Record.SourceId);
        Assert.Equal(one.Record.ContentHash, two.Record.ContentHash);
        Assert.Equal(2, store.Count);
    }

    /// <summary>
    /// A mismatch is answered, not thrown, and nothing is written: the caller has to be able to tell a truncated
    /// upload from a wrong one, and a rejected document must not leave bytes behind.
    /// </summary>
    [Fact]
    public async Task ADeclaredHashThatDoesNotMatchIsRefusedWithoutWriting()
    {
        var (ingest, store, registry) = Fixture();

        var outcome = Rejected(
            await ingest.PutAsync("acme", "docs/note.txt", "text/plain", Bytes, "sha256:0000"));

        Assert.Equal("sha256:0000", outcome.Declared);
        Assert.Equal(ArtifactContent.Hash(Bytes), outcome.Actual);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ADeclaredHashThatMatchesIsAccepted()
    {
        var (ingest, store, _) = Fixture();

        var outcome = Ingested(
            await ingest.PutAsync("acme", "docs/note.txt", "text/plain", Bytes, ArtifactContent.Hash(Bytes)));

        Assert.Equal(SourceIngestKind.New, outcome.Kind);
        Assert.Equal(1, store.Count);
    }

    /// <summary>
    /// The reserved evidence keyspace is refused before anything is looked up or written, so a document cannot be put
    /// where a sealed artifact lives.
    /// </summary>
    [Theory]
    [InlineData("evidence/note.txt")]
    [InlineData("evidence/a/b.txt")]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("docs/../../escape.txt")]
    [InlineData("docs\\windows.txt")]
    public async Task APathTheStoreMayNotHoldIsRefusedBeforeAnythingHappens(string path)
    {
        var (ingest, store, registry) = Fixture();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => ingest.PutAsync("acme", path, "text/plain", Bytes).AsTask());

        Assert.Equal(0, store.Count);
        Assert.Equal(0, registry.Count);
    }

    /// <summary>
    /// The reserved keyspace is the <c>evidence/</c> prefix, not the word: a document named <c>evidence</c> sits at
    /// the root and collides with nothing, so refusing it would be refusing a path for its name rather than for where
    /// it is. Pinned because "tighten the check" is the edit that would quietly break it.
    /// </summary>
    [Fact]
    public async Task ADocumentNamedLikeTheReservedPrefixButNotInsideItIsAllowed()
    {
        var (ingest, store, _) = Fixture();

        var outcome = Ingested(await ingest.PutAsync("acme", "evidence", "text/plain", Bytes));

        Assert.Equal(SourceIngestKind.New, outcome.Kind);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task AnEmptyMediaTypeIsRefused()
    {
        var (ingest, _, _) = Fixture();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => ingest.PutAsync("acme", "docs/note.txt", " ", Bytes).AsTask());
    }

    private static (SourceIngest Ingest, InMemorySourceStore Store, InMemorySourceRegistry Registry) Fixture()
    {
        var store = new InMemorySourceStore();
        var registry = new InMemorySourceRegistry();

        return (new SourceIngest(store, registry), store, registry);
    }

    private static SourceIngested Ingested(IngestOutcome outcome) =>
        outcome is SourceIngested ingested ? ingested : throw new InvalidOperationException("Nothing was ingested.");

    private static IngestRejected Rejected(IngestOutcome outcome) =>
        outcome is IngestRejected rejected ? rejected : throw new InvalidOperationException("Nothing was rejected.");
}
