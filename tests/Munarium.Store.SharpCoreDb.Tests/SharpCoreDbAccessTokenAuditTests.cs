namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Access;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbAccessTokenAudit"/>: who was given what, and which of those were withdrawn.
/// </summary>
/// <remarks>
/// The store's whole reason to exist is that a withdrawal outlives the process that made it, so the strongest test here
/// reopens the database rather than asking the same object again.
/// </remarks>
public class SharpCoreDbAccessTokenAuditTests
{
    /// <summary>A row comes back as it went in, and only to its own tenant.</summary>
    [Fact]
    public async Task ARecordedCapabilityIsReadBackByItsIdentity()
    {
        await using var fixture = SourceStoreFixture.Create();

        var recorded = await fixture.Audit.RecordAsync(Row("jti-1"));

        Assert.False(recorded.Revoked);

        var read = await fixture.Audit.GetAsync("acme", "jti-1");

        Assert.Equal("jti-1", read?.TokenId);
        Assert.Equal("acme", read?.Tenant);
        Assert.Equal("tyler@example.com", read?.Subject);
        Assert.Equal(3, read?.Level);
        Assert.Equal(["alpha", "beta"], read?.Compartments);
        Assert.Equal([AccessScope.Query, AccessScope.Ingest], read?.Scopes);
        Assert.Null(read?.Runbooks);
        Assert.Equal(1_760_000_000, read?.IssuedAt);
        Assert.Equal(1_760_003_600, read?.ExpiresAt);

        // The identity alone does not answer across tenants: the row is read by token id and then judged.
        Assert.Null(await fixture.Audit.GetAsync("other", "jti-1"));
        Assert.Null(await fixture.Audit.GetAsync("acme", "jti-nothing"));
    }

    /// <summary>The first withdrawal stands, and a second does not move it.</summary>
    [Fact]
    public async Task AWithdrawalStandsAndIsNotMovedByASecond()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.Audit.RecordAsync(Row("jti-1"));

        var first = await fixture.Audit.RevokeAsync("acme", "jti-1", 1_760_000_100);
        var second = await fixture.Audit.RevokeAsync("acme", "jti-1", 1_760_000_900);
        var read = await fixture.Audit.GetAsync("acme", "jti-1");

        Assert.Equal(1_760_000_100, first?.RevokedAt);
        // Compared on the instant rather than on the row: this record holds lists, and a record whose members include a
        // list compares those by reference - so Assert.Equal on two rows would fail on rows that say the same thing.
        Assert.Equal(1_760_000_100, second?.RevokedAt);
        Assert.Equal(first?.TokenId, second?.TokenId);
        Assert.True(read?.Revoked);
        Assert.Equal(1_760_000_100, read?.RevokedAt);
    }

    /// <summary>Recording a withdrawn capability again keeps the withdrawal.</summary>
    /// <remarks>
    /// The one thing this table must never become is a way back: an issuance path that re-records a capability - a retry,
    /// or a second request naming the same identity - must not clear a withdrawal somebody made.
    /// </remarks>
    [Fact]
    public async Task RecordingAWithdrawnCapabilityAgainKeepsTheWithdrawal()
    {
        await using var fixture = SourceStoreFixture.Create();
        await fixture.Audit.RecordAsync(Row("jti-1"));
        await fixture.Audit.RevokeAsync("acme", "jti-1", 1_760_000_100);

        var recorded = await fixture.Audit.RecordAsync(Row("jti-1") with { Level = 5 });
        var read = await fixture.Audit.GetAsync("acme", "jti-1");

        Assert.True(recorded.Revoked);
        Assert.Equal(5, read?.Level);
        Assert.Equal(1_760_000_100, read?.RevokedAt);
    }

    /// <summary>
    /// An absent runbook list comes back as "any" and an empty one as "none".
    /// </summary>
    /// <remarks>
    /// The two meanings that must not be confused: a capability that names no runbooks is unrestricted within its level,
    /// and one that names none at all permits nothing. A store that wrote both as empty text would turn a capability that
    /// can do nothing into one that can do anything, which is why the column is paired.
    /// </remarks>
    [Fact]
    public async Task AnAbsentRunbookListIsAnyAndAnEmptyOneIsNone()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Audit.RecordAsync(Row("jti-any"));
        await fixture.Audit.RecordAsync(Row("jti-none", runbooks: []));
        await fixture.Audit.RecordAsync(Row("jti-some", runbooks: ["reconcile", "settle"]));

        Assert.Null((await fixture.Audit.GetAsync("acme", "jti-any"))?.Runbooks);
        Assert.Empty((await fixture.Audit.GetAsync("acme", "jti-none"))?.Runbooks ?? ["unreadable"]);
        Assert.Equal(["reconcile", "settle"], (await fixture.Audit.GetAsync("acme", "jti-some"))?.Runbooks);
    }

    /// <summary>The list is one tenant's rows, newest first.</summary>
    [Fact]
    public async Task TheListIsOneTenantsRowsNewestFirst()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Audit.RecordAsync(Row("jti-old", issuedAt: 1_700_000_000));
        await fixture.Audit.RecordAsync(Row("jti-new", issuedAt: 1_770_000_000));
        await fixture.Audit.RecordAsync(Row("jti-other", tenant: "globex"));

        var rows = await fixture.Audit.ListAsync("acme");

        Assert.Equal(["jti-new", "jti-old"], rows.Select(row => row.TokenId));
    }

    /// <summary>Withdrawing something that was never issued answers nothing, and records nothing.</summary>
    [Fact]
    public async Task WithdrawingWhatWasNeverIssuedAnswersNothing()
    {
        await using var fixture = SourceStoreFixture.Create();

        Assert.Null(await fixture.Audit.RevokeAsync("acme", "jti-nothing", 1_760_000_100));
        Assert.Empty(await fixture.Audit.ListAsync("acme"));
    }

    /// <summary>A withdrawal outlives the process that made it.</summary>
    /// <remarks>
    /// The reason this is a table and not a set in memory: a deny-list that a restart forgets is not a deny-list, and the
    /// deploy that forgot it would say nothing.
    /// </remarks>
    [Fact]
    public async Task AWithdrawalOutlivesTheProcessThatMadeIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");

        await using (var first = SourceStoreFixture.Create(path))
        {
            await first.Audit.RecordAsync(Row("jti-1"));
            await first.Audit.RevokeAsync("acme", "jti-1", 1_760_000_100);
        }

        await using var reopened = SourceStoreFixture.Create(path);
        var read = await reopened.Audit.GetAsync("acme", "jti-1");

        Assert.True(read?.Revoked);
        Assert.Equal(1_760_000_100, read?.RevokedAt);
    }

    /// <summary>The row an issuance would leave behind.</summary>
    /// <param name="tokenId">The capability's identity.</param>
    /// <param name="tenant">The tenant it was minted for.</param>
    /// <param name="runbooks">The runbooks it permits, or <see langword="null"/> for any.</param>
    /// <param name="issuedAt">When it was issued.</param>
    /// <returns>The row.</returns>
    private static IssuedCapability Row(
        string tokenId,
        string tenant = "acme",
        IReadOnlyList<string>? runbooks = null,
        long issuedAt = 1_760_000_000) => new()
        {
            TokenId = tokenId,
            Tenant = tenant,
            Subject = "tyler@example.com",
            Level = 3,
            Compartments = ["alpha", "beta"],
            Scopes = [AccessScope.Query, AccessScope.Ingest],
            Runbooks = runbooks,
            IssuedAt = issuedAt,
            ExpiresAt = issuedAt + 3_600,
        };
}
