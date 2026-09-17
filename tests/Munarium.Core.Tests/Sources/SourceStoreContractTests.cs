namespace Munarium.Core.Tests.Sources;

using System.Text;
using Munarium.Core.Tests.Support;
using Munarium.Sources;

/// <summary>
/// Tests for the object-store seam's contract, driven through the in-memory double every adapter has to
/// agree with.
/// </summary>
public class SourceStoreContractTests
{
    [Fact]
    public async Task BytesWrittenAtAPathReadBackAtTheSamePath()
    {
        var store = new InMemorySourceStore();
        var key = SourceKey.New("demo", "northgate/x.md", "sha256:abc");

        var uri = await store.PutAsync(key, "text/markdown", "the supplier is north"u8.ToArray());

        Assert.Equal("mem://demo/northgate/x.md", uri);
        Assert.Equal("the supplier is north", Encoding.UTF8.GetString(await store.GetAsync(key)));
        Assert.True(await store.ExistsAsync(key));
        Assert.Equal("text/markdown", store.MediaTypeOf(key.BlobName));
    }

    [Fact]
    public async Task ABytesAreAddressedByTenantAndPathTogether()
    {
        var store = new InMemorySourceStore();
        var first = SourceKey.New("demo", "policy/x.md", "h1");
        var second = SourceKey.New("other", "policy/x.md", "h2");

        await store.PutAsync(first, "text/markdown", "demo's copy"u8.ToArray());
        await store.PutAsync(second, "text/markdown", "other's copy"u8.ToArray());

        // The same bytes at two paths are two sources, and two tenants never share a blob.
        Assert.NotEqual(first.SourceId, second.SourceId);
        Assert.Equal("demo's copy", Encoding.UTF8.GetString(await store.GetAsync(first)));
        Assert.Equal("other's copy", Encoding.UTF8.GetString(await store.GetAsync(second)));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task WritingAgainOverwritesWhatWasThere()
    {
        var store = new InMemorySourceStore();
        var key = SourceKey.New("demo", "x.md", "h1");

        await store.PutAsync(key, "text/markdown", "v1"u8.ToArray());
        await store.PutAsync(key, "text/markdown", "v2"u8.ToArray());

        Assert.Equal("v2", Encoding.UTF8.GetString(await store.GetAsync(key)));
        Assert.Equal(1, store.Count);
    }

    /// <summary>Deleting is idempotent: an absent blob is not an error, because a retry is not a bug.</summary>
    [Fact]
    public async Task DeletingTwiceSucceeds()
    {
        var store = new InMemorySourceStore();
        var key = SourceKey.New("demo", "x.md", "h1");

        await store.PutAsync(key, "text/markdown", "bytes"u8.ToArray());
        await store.DeleteAsync(key);
        await store.DeleteAsync(key);

        Assert.False(await store.ExistsAsync(key));
        Assert.Empty(await store.GetAsync(key));
    }

    [Fact]
    public void AStoreSaysWhereTheBytesWent() => Assert.Equal("mem", new InMemorySourceStore().BackendId);
}
