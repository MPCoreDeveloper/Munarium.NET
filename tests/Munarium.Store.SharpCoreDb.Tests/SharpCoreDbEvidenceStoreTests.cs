namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Evidence;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbEvidenceStore"/>: the artifact's lifecycle, the single-use grant, and the record of
/// who resolved what.
/// </summary>
public class SharpCoreDbEvidenceStoreTests
{
    [Fact]
    public async Task ASealIsReadBackByItsIdentityAndByItsDomainKey()
    {
        await using var fixture = SourceStoreFixture.Create();
        var artifact = Artifact("ev-1");

        var seal = await fixture.Evidence.RegisterAsync(artifact);

        Assert.True(seal.Created);
        Assert.Equal("ev-1", seal.EvidenceId);
        Assert.Null(seal.Grant);

        var read = await fixture.Evidence.GetAsync("acme", "ev-1");

        Assert.NotNull(read);
        Assert.Equal(EvidenceState.Committed, read.State);
        Assert.Equal(artifact.BlobPath, read.BlobPath);
        Assert.Equal("2026-09-17T00:00:00Z", read.CreatedAt);
        Assert.Equal("2026-09-17T00:00:01Z", read.CommittedAt);

        // The manifest comes back with the identity the server assigned, because a reader holding the manifest alone has
        // to be able to say which artifact it describes.
        Assert.Equal("ev-1", read.Manifest.EvidenceId);
        Assert.Equal(artifact.Manifest.ComputeDomainKey(), read.Manifest.ComputeDomainKey());

        var found = await fixture.Evidence.FindByDomainKeyAsync("acme", artifact.Manifest.ComputeDomainKey());

        Assert.Equal("ev-1", found?.EvidenceId);

        // The tenant is checked after the read, and neither an unknown id nor an unknown domain key is an error.
        Assert.Null(await fixture.Evidence.GetAsync("other", "ev-1"));
        Assert.Null(await fixture.Evidence.FindByDomainKeyAsync("other", artifact.Manifest.ComputeDomainKey()));
        Assert.Null(await fixture.Evidence.GetAsync("acme", "ev-nothing"));
    }

    /// <summary>
    /// The domain key is the idempotency tuple, so a second seal of the same logical result is the same artifact: what is
    /// recorded stays recorded, and the caller is told it did not create anything.
    /// </summary>
    [Fact]
    public async Task TheSameSealIsTheSameSeal()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Evidence.RegisterAsync(Artifact("ev-1"));

        var again = await fixture.Evidence.RegisterAsync(
            Artifact("ev-2", retention: new Retention { ExpiresAt = "2027-01-01T00:00:00Z" }));

        Assert.False(again.Created);
        Assert.Equal("ev-1", again.EvidenceId);
        Assert.Null(await fixture.Evidence.GetAsync("acme", "ev-2"));

        var read = await fixture.Evidence.GetAsync("acme", "ev-1");

        Assert.Equal("2026-09-17T00:00:01Z", read?.CommittedAt);
        Assert.Null(read?.Manifest.Retention);
    }

    [Fact]
    public async Task AGrantIsIssuedForAPendingArtifactAndSpentOnce()
    {
        await using var fixture = SourceStoreFixture.Create();

        var seal = await fixture.Evidence.RegisterAsync(
            Artifact("ev-grant", state: EvidenceState.Pending),
            Grant("ev-grant", "g-1"));

        Assert.True(seal.Created);
        Assert.Equal("g-1", seal.Grant?.GrantId);

        // A pending artifact is not evidence yet: the grant exists and the bytes do not.
        Assert.Equal(EvidenceState.Pending, (await fixture.Evidence.GetAsync("acme", "ev-grant"))?.State);

        var spent = await fixture.Evidence.ConsumeGrantAsync("acme", "ev-grant", "g-1", "2026-09-17T00:30:00Z");

        Assert.Equal("g-1", spent?.GrantId);
        Assert.Equal("2026-09-17T00:30:00Z", spent?.UsedAt);

        // Single use is the point: the second attempt is refused even inside the TTL, which is what keeps a leaked grant
        // from being a second write.
        Assert.Null(await fixture.Evidence.ConsumeGrantAsync("acme", "ev-grant", "g-1", "2026-09-17T00:31:00Z"));

        // Another tenant, another artifact and another grant are all refusals rather than somebody else's capability.
        Assert.Null(await fixture.Evidence.ConsumeGrantAsync("other", "ev-grant", "g-1", "2026-09-17T00:31:00Z"));
        Assert.Null(await fixture.Evidence.ConsumeGrantAsync("acme", "ev-other", "g-1", "2026-09-17T00:31:00Z"));
        Assert.Null(await fixture.Evidence.ConsumeGrantAsync("acme", "ev-grant", "g-nothing", "2026-09-17T00:31:00Z"));
    }

    /// <summary>
    /// An expiry is compared against the clock the caller passes, because the plane keeps no clock of its own: the instant
    /// a grant stops being usable is the first instant it is refused, and a refused attempt does not spend it.
    /// </summary>
    [Fact]
    public async Task AGrantStopsBeingUsableWhenItExpires()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Evidence.RegisterAsync(
            Artifact("ev-ttl", state: EvidenceState.Pending),
            Grant("ev-ttl", "g-ttl", "2026-09-17T00:15:00Z"));

        Assert.Null(await fixture.Evidence.ConsumeGrantAsync("acme", "ev-ttl", "g-ttl", "2026-09-17T00:15:00Z"));

        Assert.Equal(
            "g-ttl",
            (await fixture.Evidence.ConsumeGrantAsync("acme", "ev-ttl", "g-ttl", "2026-09-17T00:14:59Z"))?.GrantId);
    }

    [Fact]
    public async Task APendingArtifactBecomesCommittedOnce()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Evidence.RegisterAsync(Artifact("ev-pending", state: EvidenceState.Pending));

        Assert.Equal(EvidenceState.Pending, (await fixture.Evidence.GetAsync("acme", "ev-pending"))?.State);

        Assert.True(await fixture.Evidence.CommitAsync("acme", "ev-pending", "2026-09-17T00:05:00Z"));

        var committed = await fixture.Evidence.GetAsync("acme", "ev-pending");

        Assert.Equal(EvidenceState.Committed, committed?.State);
        Assert.Equal("2026-09-17T00:05:00Z", committed?.CommittedAt);

        // A replayed commit is visible rather than silent, and a commit for something nobody registered changes nothing.
        Assert.False(await fixture.Evidence.CommitAsync("acme", "ev-pending", "2026-09-17T00:06:00Z"));
        Assert.Equal("2026-09-17T00:05:00Z", (await fixture.Evidence.GetAsync("acme", "ev-pending"))?.CommittedAt);
        Assert.False(await fixture.Evidence.CommitAsync("acme", "ev-nothing", "2026-09-17T00:06:00Z"));
        Assert.False(await fixture.Evidence.CommitAsync("other", "ev-pending", "2026-09-17T00:06:00Z"));
    }

    [Fact]
    public async Task ASealedArtifactCannotBeHandedAGrant()
    {
        await using var fixture = SourceStoreFixture.Create();

        var refusal = await Assert.ThrowsAsync<ArgumentException>(
            async () => await fixture.Evidence.RegisterAsync(Artifact("ev-sealed"), Grant("ev-sealed", "g-1")));

        Assert.Contains("pending", refusal.Message, StringComparison.Ordinal);
        Assert.Null(await fixture.Evidence.GetAsync("acme", "ev-sealed"));
    }

    [Fact]
    public async Task TheSweepFindsWhatExpiredOldestFirstAndLeavesHoldsAlone()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Evidence.RegisterAsync(Artifact("ev-hold", retention: RetentionUntil("2026-09-20T00:00:00Z")));
        await fixture.Evidence.RegisterAsync(
            Artifact("ev-later", policy: "p-later", retention: RetentionUntil("2026-10-01T00:00:00Z")));
        await fixture.Evidence.RegisterAsync(
            Artifact("ev-sooner", policy: "p-sooner", retention: RetentionUntil("2026-09-20T00:00:00Z")));
        await fixture.Evidence.RegisterAsync(
            Artifact("ev-future", policy: "p-future", retention: RetentionUntil("2027-01-01T00:00:00Z")));
        await fixture.Evidence.RegisterAsync(Artifact("ev-none", policy: "p-none"));

        Assert.True(await fixture.Evidence.SetLegalHoldAsync("acme", "ev-hold", hold: true));

        var due = await fixture.Evidence.PurgeDueAsync("2026-11-01T00:00:00Z", limit: 10);

        // Oldest expiry first, and three things are not due: a hold, an artifact whose retention has not come round, and
        // one that declared no retention at all.
        Assert.Equal(["ev-sooner", "ev-later"], due.Select(artifact => artifact.EvidenceId));

        // The limit is a limit rather than a filter that happens to be small, and zero sweeps nothing.
        Assert.Equal(
            ["ev-sooner"],
            (await fixture.Evidence.PurgeDueAsync("2026-11-01T00:00:00Z", limit: 1))
                .Select(artifact => artifact.EvidenceId));
        Assert.Empty(await fixture.Evidence.PurgeDueAsync("2026-11-01T00:00:00Z", limit: 0));
    }

    [Fact]
    public async Task PurgingKeepsTheRowAndHappensOnce()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Evidence.RegisterAsync(
            Artifact("ev-purging", retention: RetentionUntil("2026-09-20T00:00:00Z")));

        Assert.True(await fixture.Evidence.MarkPurgedAsync("acme", "ev-purging", "2026-11-01T00:00:00Z"));

        var purged = await fixture.Evidence.GetAsync("acme", "ev-purging");

        // The row survives the purge, so a citation against it resolves as expired rather than as never having existed.
        Assert.Equal(EvidenceState.Purged, purged?.State);
        Assert.Equal("2026-11-01T00:00:00Z", purged?.Manifest.Retention?.PurgedAt);

        // Two sweeps cannot both claim the row, a purged artifact is no longer due, and a commit arriving after the bytes
        // are gone cannot un-delete them - calling that committed would be the lie the row exists to prevent.
        Assert.False(await fixture.Evidence.MarkPurgedAsync("acme", "ev-purging", "2026-11-02T00:00:00Z"));
        Assert.Empty(await fixture.Evidence.PurgeDueAsync("2026-11-02T00:00:00Z", limit: 10));
        Assert.False(await fixture.Evidence.CommitAsync("acme", "ev-purging", "2026-11-02T00:00:00Z"));
        Assert.Equal(EvidenceState.Purged, (await fixture.Evidence.GetAsync("acme", "ev-purging"))?.State);
    }

    /// <summary>
    /// A hold blocks deletion and never reading: an instruction to preserve evidence that also hid it would be a strange
    /// one, and a read stays governed by the authorization class exactly as before.
    /// </summary>
    [Fact]
    public async Task AHoldBlocksDeletionAndNotReading()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Evidence.RegisterAsync(
            Artifact("ev-held", retention: RetentionUntil("2026-09-20T00:00:00Z")));

        Assert.True(await fixture.Evidence.SetLegalHoldAsync("acme", "ev-held", hold: true));
        Assert.False(await fixture.Evidence.MarkPurgedAsync("acme", "ev-held", "2026-11-01T00:00:00Z"));
        Assert.Empty(await fixture.Evidence.PurgeDueAsync("2026-11-01T00:00:00Z", limit: 10));

        var held = await fixture.Evidence.GetAsync("acme", "ev-held");

        Assert.Equal(EvidenceState.Committed, held?.State);
        Assert.True(held?.Manifest.Retention?.LegalHold);
        Assert.Null(held?.Manifest.Retention?.PurgedAt);

        // Lifting the hold puts it back in the sweep, and an artifact nobody registered has nothing to hold.
        Assert.True(await fixture.Evidence.SetLegalHoldAsync("acme", "ev-held", hold: false));
        Assert.Single(await fixture.Evidence.PurgeDueAsync("2026-11-01T00:00:00Z", limit: 10));
        Assert.False(await fixture.Evidence.SetLegalHoldAsync("acme", "ev-nothing", hold: true));

        // A hold on an artifact sealed without retention is still a hold: preserving something does not depend on the
        // producer having declared when it would have expired.
        await fixture.Evidence.RegisterAsync(Artifact("ev-no-retention", policy: "p-no-retention"));

        Assert.True(await fixture.Evidence.SetLegalHoldAsync("acme", "ev-no-retention", hold: true));
        Assert.True((await fixture.Evidence.GetAsync("acme", "ev-no-retention"))?.Manifest.Retention?.LegalHold);
    }

    [Fact]
    public async Task WhatWasReadIsRecordedNewestFirst()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Evidence.RegisterAsync(Artifact("ev-read"));

        await fixture.Evidence.RecordAccessAsync(
            Access("ev-read", "alice", "manifest", "ok", "2026-09-17T00:00:01Z"));
        await fixture.Evidence.RecordAccessAsync(
            Access("ev-read", "bob", "rows", "denied", "2026-09-17T00:00:02Z", rowFrom: 10, rowLimit: 5));
        await fixture.Evidence.RecordAccessAsync(
            Access("ev-read", "carol", "rows", "ok", "2026-09-17T00:00:03Z"));
        await fixture.Evidence.RecordAccessAsync(
            Access("ev-read", "dave", "manifest", "ok", "2026-09-17T00:00:04Z", tenant: "other"));

        var recent = await fixture.Evidence.AccessesAsync("acme", "ev-read", limit: 10);

        // Newest first, and only this tenant's reads of this artifact: an audit that mixed tenants would answer a question
        // nobody asked. What was read is recorded, never the rows - an audit table holding the regulated data it audits
        // would be a second copy of the problem the audit exists to describe.
        Assert.Equal(["carol", "bob", "alice"], recent.Select(access => access.Uid));
        Assert.Equal("denied", recent[1].Outcome);
        Assert.Equal("rows", recent[1].Kind);
        Assert.Equal(10, recent[1].RowFrom);
        Assert.Equal(5, recent[1].RowLimit);

        // A read that named no first row and no limit is stored without them, because zero and none say the same thing
        // about a read.
        Assert.Null(recent[0].RowFrom);
        Assert.Null(recent[0].RowLimit);

        Assert.Equal(
            ["carol", "bob"],
            (await fixture.Evidence.AccessesAsync("acme", "ev-read", limit: 2)).Select(access => access.Uid));
        Assert.Empty(await fixture.Evidence.AccessesAsync("acme", "ev-read", limit: 0));
        Assert.Empty(await fixture.Evidence.AccessesAsync("acme", "ev-nothing", limit: 10));
    }

    /// <summary>
    /// The plane outlives the connection that wrote it: over a restart an artifact, its domain key, its grant, its
    /// lifecycle and its accesses all have to still be there, or a deployment would lose the record of what it sealed
    /// every time it was deployed.
    /// </summary>
    [Fact]
    public async Task ThePlaneOutlivesTheConnectionThatWroteIt()
    {
        var fixture = SourceStoreFixture.Create();
        var path = fixture.DatabasePath;

        await fixture.Evidence.RegisterAsync(
            Artifact("ev-persisted", state: EvidenceState.Pending, policy: "p-persisted"),
            Grant("ev-persisted", "g-persisted"));

        await fixture.DisposeAsync();

        await using var reopened = SourceStoreFixture.Create(path);

        var read = await reopened.Evidence.GetAsync("acme", "ev-persisted");

        Assert.Equal(EvidenceState.Pending, read?.State);

        var domainKey = read!.Manifest.ComputeDomainKey();

        Assert.Equal("ev-persisted", (await reopened.Evidence.FindByDomainKeyAsync("acme", domainKey))?.EvidenceId);

        // A grant survives too, or a restart would refuse an upload the server had already authorized.
        Assert.Equal(
            "g-persisted",
            (await reopened.Evidence.ConsumeGrantAsync("acme", "ev-persisted", "g-persisted", "2026-09-17T00:10:00Z"))
                ?.GrantId);

        Assert.True(await reopened.Evidence.CommitAsync("acme", "ev-persisted", "2026-09-17T00:11:00Z"));

        await reopened.Evidence.RecordAccessAsync(
            Access("ev-persisted", "alice", "manifest", "ok", "2026-09-17T00:12:00Z"));

        Assert.Single(await reopened.Evidence.AccessesAsync("acme", "ev-persisted", limit: 10));
        Assert.True(await reopened.Evidence.SetLegalHoldAsync("acme", "ev-persisted", hold: true));
    }

    private static EvidenceArtifact Artifact(
        string evidenceId,
        string tenant = "acme",
        EvidenceState state = EvidenceState.Committed,
        Retention? retention = null,
        string policy = "policy-1") => new()
    {
        EvidenceId = evidenceId,
        Tenant = tenant,
        State = state,
        Manifest = Manifest(tenant, policy) with { Retention = retention },
        BlobPath = $"{EvidenceContract.PathPrefix}{evidenceId}",
        CreatedAt = "2026-09-17T00:00:00Z",
        CommittedAt = state == EvidenceState.Committed ? "2026-09-17T00:00:01Z" : null,
    };

    private static EvidenceGrant Grant(
        string evidenceId,
        string grantId,
        string expiresAt = "2026-09-17T01:00:00Z",
        string tenant = "acme") => new()
    {
        GrantId = grantId,
        EvidenceId = evidenceId,
        Tenant = tenant,
        ExpiresAt = expiresAt,
    };

    private static EvidenceAccess Access(
        string evidenceId,
        string uid,
        string kind,
        string outcome,
        string at,
        long? rowFrom = null,
        long? rowLimit = null,
        string tenant = "acme") => new()
    {
        EvidenceId = evidenceId,
        Tenant = tenant,
        Uid = uid,
        Kind = kind,
        RowFrom = rowFrom,
        RowLimit = rowLimit,
        Outcome = outcome,
        At = at,
    };

    private static Retention RetentionUntil(string expiresAt) => new() { ExpiresAt = expiresAt };

    /// <summary>Builds a contract-valid manifest, varying what the tenant and the policy make unique about a seal.</summary>
    private static EvidenceManifest Manifest(string tenant, string policy) => new()
    {
        ContractVersion = EvidenceContract.Version,
        Canon = EvidenceContract.Canon,
        Tenant = tenant,
        Kind = EvidenceKind.Table,
        LogicalResultHash = Hash('b'),
        ArtifactHash = Hash('c'),
        BytesLength = 12,
        MediaType = EvidenceContract.MediaTypeCsv,
        Source = new SourceRef("src-1", 3, "postgres"),
        Versions = new Versions { Policy = policy },
        Schema = new EvidenceSchema([new EvidenceColumn { Id = "col-1", Name = "region", Type = ColumnType.Text }]),
        Identity = new EvidenceIdentity(RowIdRule.Keys),
        Completeness = new Completeness { Truncated = false },
        SnapshotVector = [new SnapshotMarker { SourceId = "src-1", ReplayLevel = "source_time_travel" }],
        Execution = new Execution { StartedAt = "2026-09-17T00:00:00Z", EndedAt = "2026-09-17T00:00:01Z" },
        AuthorizationClass = new AuthorizationClass { AccessLevel = 3 },
    };

    private static string Hash(char fill) => "sha256:" + new string(fill, 64);
}
