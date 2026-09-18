namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Sources;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbSourceStore"/>: the bytes a document is, keyed by tenant and path.
/// </summary>
public class SharpCoreDbSourceStoreTests
{
    private static readonly byte[] Bytes = "The Bell rang twice."u8.ToArray();

    [Fact]
    public async Task WhatWasWrittenIsReadBackByteForByte()
    {
        await using var fixture = SourceStoreFixture.Create();
        var key = SourceKey.New("acme", "docs/note.txt", "sha256:x");

        var uri = await fixture.Store.PutAsync(key, "text/plain", Bytes);

        Assert.Equal("scdb://acme/docs/note.txt", uri);
        Assert.True(await fixture.Store.ExistsAsync(key));
        Assert.Equal(Bytes, await fixture.Store.GetAsync(key));
        Assert.Equal("sharpcoredb", fixture.Store.BackendId);
    }

    /// <summary>Base64 in a text column has to carry every byte, including the ones no text encoding would.</summary>
    [Fact]
    public async Task EveryByteSurvivesIncludingTheOnesThatAreNotText()
    {
        await using var fixture = SourceStoreFixture.Create();
        var key = SourceKey.New("acme", "docs/raw.bin", "sha256:x");
        var bytes = new byte[] { 0x00, 0xFF, 0x25, 0x50, 0x0A, 0x3B, 0x27 };

        await fixture.Store.PutAsync(key, "application/octet-stream", bytes);

        Assert.Equal(bytes, await fixture.Store.GetAsync(key));
    }

    /// <summary>
    /// A path is caller-supplied, so it is the one value that can carry a quote - and a quote that is not escaped is
    /// how a document ends up somewhere else, or how the statement stops being the statement that was meant.
    /// </summary>
    [Fact]
    public async Task APathWithAQuoteSurvives()
    {
        await using var fixture = SourceStoreFixture.Create();
        var key = SourceKey.New("acme", "docs/o'brien.txt", "sha256:x");

        await fixture.Store.PutAsync(key, "text/plain", Bytes);

        Assert.True(await fixture.Store.ExistsAsync(key));
        Assert.Equal(Bytes, await fixture.Store.GetAsync(key));
        Assert.Equal([], await fixture.Store.GetAsync(SourceKey.New("acme", "docs/obrien.txt", "sha256:x")));
    }

    [Fact]
    public async Task TwoTenantsWithTheSamePathDoNotSeeEachOthersBytes()
    {
        await using var fixture = SourceStoreFixture.Create();
        var acme = SourceKey.New("acme", "docs/note.txt", "sha256:x");
        var other = SourceKey.New("other", "docs/note.txt", "sha256:x");

        await fixture.Store.PutAsync(acme, "text/plain", Bytes);

        Assert.False(await fixture.Store.ExistsAsync(other));
        Assert.Empty(await fixture.Store.GetAsync(other));
    }

    [Fact]
    public async Task APathHoldsOneDocumentRatherThanAVersionHistory()
    {
        await using var fixture = SourceStoreFixture.Create();
        var key = SourceKey.New("acme", "docs/note.txt", "sha256:x");

        await fixture.Store.PutAsync(key, "text/plain", Bytes);
        var replacement = "The Bell rang three times."u8.ToArray();
        await fixture.Store.PutAsync(key, "text/plain", replacement);

        Assert.Equal(replacement, await fixture.Store.GetAsync(key));
    }

    [Fact]
    public async Task DeletingIsIdempotent()
    {
        await using var fixture = SourceStoreFixture.Create();
        var key = SourceKey.New("acme", "docs/note.txt", "sha256:x");
        await fixture.Store.PutAsync(key, "text/plain", Bytes);

        await fixture.Store.DeleteAsync(key);
        await fixture.Store.DeleteAsync(key);

        Assert.False(await fixture.Store.ExistsAsync(key));
    }

    [Fact]
    public async Task AnAbsentBlobReadsAsNoBytes()
    {
        await using var fixture = SourceStoreFixture.Create();

        Assert.Empty(await fixture.Store.GetAsync(SourceKey.New("acme", "docs/missing.txt", "sha256:x")));
    }

    /// <summary>
    /// The bytes outlive the connection that wrote them: a deployment that restarts has to find its documents where it
    /// left them, or every citation it ever issued points at nothing.
    /// </summary>
    [Fact]
    public async Task TheBytesAreStillThereAfterTheDatabaseIsReopened()
    {
        var fixture = SourceStoreFixture.Create();
        var key = SourceKey.New("acme", "docs/note.txt", "sha256:x");
        var path = fixture.DatabasePath;

        await fixture.Store.PutAsync(key, "text/plain", Bytes);
        await fixture.DisposeAsync();

        await using var reopened = SourceStoreFixture.Create(path);

        Assert.Equal(Bytes, await reopened.Store.GetAsync(key));
    }
}
