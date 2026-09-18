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
    /// A prefix is filtered here rather than in SQL, because a path may contain '%' - and a LIKE that treated it as a
    /// wildcard would hand a collection documents outside the prefix it bound.
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
