namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Sources;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbSourceRegistry"/>: the rows that say which path holds which bytes.
/// </summary>
public class SharpCoreDbSourceRegistryTests
{
    [Fact]
    public async Task ARowIsReadBackByPathAndByIdentity()
    {
        await using var fixture = SourceStoreFixture.Create();
        var record = Row("docs/note.txt", "sha256:abc");

        var stamped = await fixture.Registry.RecordAsync(record);

        Assert.NotNull(stamped.IngestedAt);
        Assert.Equal(record.SourceId, stamped.SourceId);

        var byPath = await fixture.Registry.FindAsync("acme", "docs/note.txt");
        Assert.Equal("sha256:abc", byPath?.ContentHash);
        Assert.Equal("sharpcoredb", byPath?.BackendId);
        Assert.Equal("scdb://acme/docs/note.txt", byPath?.BlobUri);
        Assert.Equal(12, byPath?.BytesLength);

        var byId = await fixture.Registry.GetAsync("acme", record.SourceId);
        Assert.Equal("docs/note.txt", byId?.Path);
        Assert.Equal(stamped, byId);
    }

    /// <summary>
    /// A path is a source's identity, so recording it again replaces one row rather than adding a second - and the hash
    /// moves, which is exactly what an index build notices.
    /// </summary>
    [Fact]
    public async Task RecordingTheSamePathAgainReplacesTheRow()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.Registry.RecordAsync(Row("docs/note.txt", "sha256:abc"));

        var replaced = await fixture.Registry.RecordAsync(Row("docs/note.txt", "sha256:def"));

        var rows = await fixture.Registry.ListAsync("acme");
        Assert.Single(rows);
        Assert.Equal("sha256:def", rows[0].ContentHash);
        Assert.Equal(replaced.SourceId, rows[0].SourceId);
    }

    [Fact]
    public async Task APrefixSelectsTheSourcesACollectionBinds()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.Registry.RecordAsync(Row("docs/a.txt", "sha256:a"));
        await fixture.Registry.RecordAsync(Row("docs/nested/b.txt", "sha256:b"));
        await fixture.Registry.RecordAsync(Row("other/c.txt", "sha256:c"));

        var docs = await fixture.Registry.ListAsync("acme", "docs/");

        Assert.Equal(["docs/a.txt", "docs/nested/b.txt"], docs.Select(row => row.Path));
        Assert.Equal(3, (await fixture.Registry.ListAsync("acme")).Count);
    }

    /// <summary>
    /// A prefix is filtered here rather than in SQL, because measured, LIKE treats '%' in the pattern as a wildcard - in
    /// a literal and in a bound parameter alike - and a wildcard would hand a collection documents outside the prefix it
    /// bound.
    /// </summary>
    [Fact]
    public async Task APrefixContainingAWildcardCharacterMatchesOnlyItself()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.Registry.RecordAsync(Row("docs/budget%2026.txt", "sha256:a"));
        await fixture.Registry.RecordAsync(Row("docs/budget2026.txt", "sha256:b"));

        var matched = await fixture.Registry.ListAsync("acme", "docs/budget%");

        Assert.Equal(["docs/budget%2026.txt"], matched.Select(row => row.Path));
    }

    [Fact]
    public async Task APathWithAQuoteSurvives()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Registry.RecordAsync(Row("docs/o'brien.txt", "sha256:a"));

        var row = await fixture.Registry.FindAsync("acme", "docs/o'brien.txt");
        Assert.Equal("docs/o'brien.txt", row?.Path);
        Assert.Single(await fixture.Registry.ListAsync("acme"));
    }

    [Fact]
    public async Task OneTenantCannotReachAnotherTenantsRows()
    {
        await using var fixture = SourceStoreFixture.Create();
        var acme = Row("docs/note.txt", "sha256:a");
        await fixture.Registry.RecordAsync(acme);

        Assert.Null(await fixture.Registry.FindAsync("other", "docs/note.txt"));
        Assert.Null(await fixture.Registry.GetAsync("other", acme.SourceId));
        Assert.Empty(await fixture.Registry.ListAsync("other"));
    }

    [Fact]
    public async Task APathThatWasNeverIngestedHasNoRow()
    {
        await using var fixture = SourceStoreFixture.Create();

        Assert.Null(await fixture.Registry.FindAsync("acme", "docs/missing.txt"));
        Assert.Null(await fixture.Registry.GetAsync("acme", "src-0000000000000000"));
    }

    /// <summary>
    /// The two extraction fields round-trip, and a write-back changes them and nothing else.
    /// </summary>
    /// <remarks>
    /// This is the original's UPDATE at index time, and the reason it is an operation of its own: a row is written when a
    /// document is uploaded and its extraction is known later, so the write-back must not rewrite what it was not asked to
    /// change.
    /// </remarks>
    [Fact]
    public async Task AnExtractionOutcomeIsWrittenBackWithoutRewritingTheRow()
    {
        await using var fixture = SourceStoreFixture.Create();
        var record = Row("docs/note.txt", "sha256:abc");

        await fixture.Registry.RecordAsync(record);

        var recorded = await fixture.Registry.RecordExtractionAsync("acme", record.SourceId, "empty", "pdf-text");

        Assert.Equal("empty", recorded?.ExtractionStatus);
        Assert.Equal("pdf-text", recorded?.ExtractionMethod);

        // The rest of the row is untouched, hash included: the write-back is about two fields.
        Assert.Equal("sha256:abc", recorded?.ContentHash);
        Assert.Equal(record.BlobUri, recorded?.BlobUri);

        var read = await fixture.Registry.FindAsync("acme", "docs/note.txt");

        Assert.Equal("empty", read?.ExtractionStatus);

        // A write-back does not create a source that does not exist.
        Assert.Null(await fixture.Registry.RecordExtractionAsync("acme", "src-nothing", "ok", "text"));

        // And both fields can be cleared, which is what a re-upload owes: the new bytes have not been read yet.
        var cleared = await fixture.Registry.RecordExtractionAsync("acme", record.SourceId, null, null);

        Assert.Null(cleared?.ExtractionStatus);
        Assert.Null(cleared?.ExtractionMethod);
    }

    private static SourceRecord Row(string path, string hash) => new()
    {
        Tenant = "acme",
        SourceId = SourceKey.Id("acme", path),
        Path = path,
        ContentHash = hash,
        MediaType = "text/plain",
        BytesLength = 12,
        BlobUri = $"scdb://acme/{path}",
        BackendId = "sharpcoredb",
    };
}
