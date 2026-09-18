namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Ledger;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbIdempotencyStore"/>: what a command answered, kept under the key the caller sent.
/// </summary>
public class SharpCoreDbIdempotencyStoreTests
{
    private static readonly string Key = LedgerIds.New();

    [Fact]
    public async Task WhatACommandAnsweredIsReadBackByItsKey()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Idempotency.RecordAsync("acme", "claims/v1", Key, """{"status":"accepted"}""");

        Assert.Equal("""{"status":"accepted"}""", await fixture.Idempotency.FindAsync("acme", "claims/v1", Key));
    }

    /// <summary>
    /// The scope and the tenant are part of the key's identity: the same key on another operation or version is another
    /// request, and one tenant's key cannot answer another's.
    /// </summary>
    [Fact]
    public async Task AKeyIsScopedAndBelongsToItsTenant()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.Idempotency.RecordAsync("acme", "claims/v1", Key, "answered");

        Assert.Null(await fixture.Idempotency.FindAsync("acme", "claim-batches/v1", Key));
        Assert.Null(await fixture.Idempotency.FindAsync("acme", "claims/v2", Key));
        Assert.Null(await fixture.Idempotency.FindAsync("other", "claims/v1", Key));
        Assert.Null(await fixture.Idempotency.FindAsync("acme", "claims/v1", LedgerIds.New()));
    }

    /// <summary>
    /// The first answer is the answer: a second record under the same key is ignored, because two answers to one request
    /// is the situation the seam exists to prevent.
    /// </summary>
    [Fact]
    public async Task TheFirstAnswerIsTheAnswer()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Idempotency.RecordAsync("acme", "claims/v1", Key, "first");
        await fixture.Idempotency.RecordAsync("acme", "claims/v1", Key, "second");

        Assert.Equal("first", await fixture.Idempotency.FindAsync("acme", "claims/v1", Key));
    }

    /// <summary>
    /// The answers outlive the connection that wrote them, or a retry after a restart would be done a second time.
    /// </summary>
    [Fact]
    public async Task TheAnswersOutliveTheConnectionThatWroteThem()
    {
        var fixture = SourceStoreFixture.Create();
        var path = fixture.DatabasePath;

        await fixture.Idempotency.RecordAsync("acme", "claims/v1", Key, "first");
        await fixture.DisposeAsync();

        await using var reopened = SourceStoreFixture.Create(path);

        Assert.Equal("first", await reopened.Idempotency.FindAsync("acme", "claims/v1", Key));
    }
}
