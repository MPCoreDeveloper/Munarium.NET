namespace Munarium.Server.Tests;

using System.Text;
using Munarium.Evidence;
using Munarium.Wire;

/// <summary>
/// Tests for the sealed evidence plane as it behaves, which is what both transports are adapters over: what a caller may
/// seal, what a caller may read, and what a caller learns when refused.
/// </summary>
public class EvidencePlaneTests
{
    [Fact]
    public async Task ASealedArtifactResolvesToItsManifestAndItsRows()
    {
        await using var kernel = Kernel();
        var operations = kernel.Operations;
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);
        var (manifest, bytes) = Sealed();

        var seal = Sealed(await operations.SealEvidenceAsync(
            new WireSealEvidenceRequest(manifest, Convert.ToBase64String(bytes)), principal));

        Assert.True(seal.Created);
        Assert.Equal(EvidenceState.Committed.ToWireName(), seal.State);
        Assert.StartsWith(EvidenceIds.Prefix, seal.EvidenceId, StringComparison.Ordinal);
        Assert.Null(seal.Grant);

        // The manifest resolves with the identity the server assigned stamped onto it, and with the identity a later
        // reader compares against intact.
        var read = Readable(await operations.ReadEvidenceManifestAsync(principal, seal.EvidenceId));

        Assert.Equal(seal.EvidenceId, read.EvidenceId);
        Assert.Equal(manifest.LogicalResultHash, read.LogicalResultHash);
        Assert.Equal(manifest.ComputeDomainKey(), read.ComputeDomainKey());
        Assert.Equal(EvidenceKind.Table, read.Kind);

        var rows = Rows(await operations.ReadEvidenceRowsAsync(principal, seal.EvidenceId));

        Assert.Equal(1, rows.Total);
        Assert.False(rows.HasMore);

        // The header is dropped and the manifest's column names are used: the schema is the contract, and a header that
        // disagreed with it would be the schema drifting silently.
        var row = Assert.Single(rows.Rows);

        Assert.Equal("v-1", row["vendor_id"]);
        Assert.Equal("approved", row["status"]);

        var accesses = Accesses(await operations.ReadEvidenceAccessesAsync(MunariumKernel.Tenant, seal.EvidenceId));

        Assert.Contains(accesses.Accesses, access => access.Kind == "manifest" && access.Outcome == "ok");
        Assert.Contains(accesses.Accesses, access => access.Kind == "rows" && access.Outcome == "ok");
    }

    /// <summary>
    /// The domain tuple is the idempotency layer here, and a stronger one than a header: the same logical result under
    /// the same policy and class is the same seal, across replicas and across retries.
    /// </summary>
    [Fact]
    public async Task TheSameSealIsTheSameSeal()
    {
        await using var kernel = Kernel();
        var operations = kernel.Operations;
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);

        var (manifest, bytes) = Sealed();
        var first = Sealed(await operations.SealEvidenceAsync(
            new WireSealEvidenceRequest(manifest, Convert.ToBase64String(bytes)), principal));

        var (same, sameBytes) = Sealed();
        var second = Sealed(await operations.SealEvidenceAsync(
            new WireSealEvidenceRequest(same, Convert.ToBase64String(sameBytes)), principal));

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.EvidenceId, second.EvidenceId);
    }

    /// <summary>Bytes that are not what the manifest declares are refused before anything is stored anywhere.</summary>
    [Fact]
    public async Task BytesThatAreNotWhatTheManifestDeclaresAreRefused()
    {
        await using var kernel = Kernel();
        var operations = kernel.Operations;
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);
        var (manifest, _) = Sealed();

        var refusal = Problem(await operations.SealEvidenceAsync(
            new WireSealEvidenceRequest(manifest, Convert.ToBase64String(Encoding.UTF8.GetBytes("v-9,denied\n"))),
            principal));

        Assert.Equal(MunariumOperations.EvidenceHashMismatchProblem, refusal.Type);
        Assert.Equal(409, refusal.Status);

        // The refusal names what it compared, because an operator chasing a mismatch needs both values.
        Assert.Contains(manifest.ArtifactHash, refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASealWithNoBytesIssuesAGrantThatBuysExactlyOneUpload()
    {
        await using var kernel = Kernel();
        var operations = kernel.Operations;
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);
        var (manifest, bytes) = Sealed();

        var seal = Sealed(await operations.SealEvidenceAsync(new WireSealEvidenceRequest(manifest), principal));

        Assert.True(seal.Created);
        Assert.Equal(EvidenceState.Pending.ToWireName(), seal.State);

        var grant = seal.Grant ?? throw new InvalidOperationException("a seal with no bytes has to issue a grant.");

        Assert.StartsWith(EvidenceIds.GrantPrefix, grant.GrantId, StringComparison.Ordinal);

        // A pending artifact is not evidence yet, and the resolution is recorded as denied.
        var pending = Problem(await operations.ReadEvidenceManifestAsync(principal, seal.EvidenceId));

        Assert.Equal(MunariumOperations.EvidencePendingProblem, pending.Type);
        Assert.Equal(409, pending.Status);

        // Committing before the bytes are there is the caller's state to fix, and the refusal says so.
        var early = Problem(await operations.CommitEvidenceAsync(principal, seal.EvidenceId));

        Assert.Equal(MunariumOperations.EvidenceNotCommittedProblem, early.Type);

        Assert.Null(await operations.PutEvidenceBytesAsync(principal, seal.EvidenceId, grant.GrantId, bytes));

        var committed = Committed(await operations.CommitEvidenceAsync(principal, seal.EvidenceId));

        Assert.True(committed.Committed);
        Assert.Equal(EvidenceState.Committed.ToWireName(), committed.State);

        Assert.Equal(seal.EvidenceId, Readable(await operations.ReadEvidenceManifestAsync(principal, seal.EvidenceId)).EvidenceId);

        // Single use: the second upload is refused even though the grant has not expired, which is what keeps a leaked
        // grant from being a second write.
        var reused = await operations.PutEvidenceBytesAsync(principal, seal.EvidenceId, grant.GrantId, bytes);

        Assert.Equal(MunariumOperations.EvidenceGrantInvalidProblem, reused?.Type);
    }

    /// <summary>
    /// A corrupt upload is the caller's mistake to fix, so it must not cost them the capability: the bytes are verified
    /// before the grant is spent, which turns an unrecoverable error into a retryable one.
    /// </summary>
    [Fact]
    public async Task ACorruptUploadDoesNotSpendTheGrant()
    {
        await using var kernel = Kernel();
        var operations = kernel.Operations;
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);
        var (manifest, bytes) = Sealed();

        var seal = Sealed(await operations.SealEvidenceAsync(new WireSealEvidenceRequest(manifest), principal));
        var grant = seal.Grant ?? throw new InvalidOperationException("a seal with no bytes has to issue a grant.");

        var refused = await operations.PutEvidenceBytesAsync(
            principal, seal.EvidenceId, grant.GrantId, Encoding.UTF8.GetBytes("v-9,denied\n"));

        Assert.Equal(MunariumOperations.EvidenceHashMismatchProblem, refused?.Type);

        Assert.Null(await operations.PutEvidenceBytesAsync(principal, seal.EvidenceId, grant.GrantId, bytes));
        Assert.True(Committed(await operations.CommitEvidenceAsync(principal, seal.EvidenceId)).Committed);
    }

    /// <summary>An unknown artifact answers what an invalid grant answers, so a caller learns nothing about ids.</summary>
    [Fact]
    public async Task AnUnknownArtifactAndAnInvalidGrantAnswerTheSame()
    {
        await using var kernel = Kernel();
        var operations = kernel.Operations;
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);

        var unknownArtifact = await operations.PutEvidenceBytesAsync(
            principal, "ev-00000000000000000000000000000000", "gr-00000000000000000000000000000000", default);

        var unknownGrant = await operations.PutEvidenceBytesAsync(
            principal, "ev-00000000000000000000000000000000", "gr-11111111111111111111111111111111", default);

        Assert.Equal(MunariumOperations.EvidenceGrantInvalidProblem, unknownArtifact?.Type);
        Assert.Equal(unknownArtifact?.Detail, unknownGrant?.Detail);
    }

    /// <summary>
    /// A purged artifact keeps its row, so a citation resolves as expired rather than as never having existed - the
    /// difference between "this was sealed and then lawfully removed" and "you made this up".
    /// </summary>
    [Fact]
    public async Task APurgedArtifactResolvesAsExpiredRatherThanMissing()
    {
        await using var kernel = Kernel();
        var operations = kernel.Operations;
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);
        var (manifest, bytes) = Sealed();

        var seal = Sealed(await operations.SealEvidenceAsync(
            new WireSealEvidenceRequest(manifest, Convert.ToBase64String(bytes)), principal));

        var purge = Purged(await operations.PurgeEvidenceAsync(MunariumKernel.Tenant, seal.EvidenceId));

        Assert.True(purge.Purged);
        Assert.Equal(EvidenceState.Purged.ToWireName(), purge.State);

        var expired = Problem(await operations.ReadEvidenceManifestAsync(principal, seal.EvidenceId));

        Assert.Equal(MunariumOperations.EvidenceExpiredProblem, expired.Type);
        Assert.Equal(410, expired.Status);

        Assert.Equal(
            MunariumOperations.EvidenceExpiredProblem,
            Problem(await operations.ReadEvidenceRowsAsync(principal, seal.EvidenceId)).Type);

        // The audit says expired rather than denied: retention is the honest reason it no longer resolves.
        var accesses = Accesses(await operations.ReadEvidenceAccessesAsync(MunariumKernel.Tenant, seal.EvidenceId));

        Assert.Contains(accesses.Accesses, access => access.Outcome == "expired");

        // A second purge changes nothing, and says so rather than failing.
        Assert.False(Purged(await operations.PurgeEvidenceAsync(MunariumKernel.Tenant, seal.EvidenceId)).Purged);
    }

    [Fact]
    public async Task AHoldBlocksDeletionAndNotReading()
    {
        await using var kernel = Kernel();
        var operations = kernel.Operations;
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);
        var (manifest, bytes) = Sealed();

        var seal = Sealed(await operations.SealEvidenceAsync(
            new WireSealEvidenceRequest(manifest, Convert.ToBase64String(bytes)), principal));

        Assert.Null(await operations.SetEvidenceLegalHoldAsync(MunariumKernel.Tenant, seal.EvidenceId, hold: true));

        Assert.Equal(
            MunariumOperations.EvidenceOnHoldProblem,
            Problem(await operations.PurgeEvidenceAsync(MunariumKernel.Tenant, seal.EvidenceId)).Type);

        // A hold blocks deletion and nothing else: an instruction to preserve evidence that also hid it would be a
        // strange instruction, so the artifact still resolves for a reader who may read it.
        Assert.Equal(
            seal.EvidenceId,
            Readable(await operations.ReadEvidenceManifestAsync(principal, seal.EvidenceId)).EvidenceId);

        Assert.Null(await operations.SetEvidenceLegalHoldAsync(MunariumKernel.Tenant, seal.EvidenceId, hold: false));
        Assert.True(Purged(await operations.PurgeEvidenceAsync(MunariumKernel.Tenant, seal.EvidenceId)).Purged);

        // A hold on an artifact nobody sealed names nothing to hold.
        Assert.Equal(
            MunariumOperations.EvidenceNotFoundProblem,
            (await operations.SetEvidenceLegalHoldAsync(MunariumKernel.Tenant, "ev-nothing", hold: true))?.Type);
    }

    /// <summary>
    /// A refusal says nothing about what is behind it: learning "this exists and is above you" is itself a disclosure,
    /// and so is learning which class it is in. Sealing above oneself is refused for the same reason from the other side.
    /// </summary>
    [Fact]
    public async Task ARefusalSaysNothingAboutWhatIsBehindIt()
    {
        await using var kernel = Kernel();
        var operations = kernel.Operations;
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);
        var (manifest, bytes) = Sealed();

        var seal = Sealed(await operations.SealEvidenceAsync(
            new WireSealEvidenceRequest(manifest, Convert.ToBase64String(bytes)), principal));

        var narrow = principal with { Level = 1 };
        var forbidden = Problem(await operations.ReadEvidenceManifestAsync(narrow, seal.EvidenceId));

        Assert.Equal(MunariumOperations.EvidenceForbiddenProblem, forbidden.Type);
        Assert.Equal(403, forbidden.Status);
        Assert.DoesNotContain(manifest.LogicalResultHash, forbidden.Detail, StringComparison.Ordinal);

        Assert.Equal(
            MunariumOperations.EvidenceForbiddenProblem,
            Problem(await operations.SealEvidenceAsync(new WireSealEvidenceRequest(manifest), narrow)).Type);
    }

    /// <summary>
    /// The plane outlives the connection that sealed it: over a restart the artifact, its bytes and its audit have to
    /// still be there, or a citation into yesterday's answer would resolve to nothing.
    /// </summary>
    [Fact]
    public async Task ThePlaneOutlivesTheConnectionThatSealedIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");
        var shapes = MunariumShapeBundles.Load(MunariumApiFactory.ShapesDirectory);
        var principal = EvidencePrincipal.ForDeployment(MunariumKernel.Tenant);
        string evidenceId;

        await using (var first = MunariumKernel.Create(path, "munarium-evidence", shapes))
        {
            var (manifest, bytes) = Sealed();

            evidenceId = Sealed(await first.Operations.SealEvidenceAsync(
                new WireSealEvidenceRequest(manifest, Convert.ToBase64String(bytes)), principal)).EvidenceId;
        }

        await using var second = MunariumKernel.Create(path, "munarium-evidence", shapes);

        Assert.Equal(
            evidenceId,
            Readable(await second.Operations.ReadEvidenceManifestAsync(principal, evidenceId)).EvidenceId);

        // The rows come back out of the bytes, which have to have outlived the process as well.
        Assert.Equal(1, Rows(await second.Operations.ReadEvidenceRowsAsync(principal, evidenceId)).Total);
        Assert.Contains(
            Accesses(await second.Operations.ReadEvidenceAccessesAsync(MunariumKernel.Tenant, evidenceId)).Accesses,
            access => access.Kind == "rows");
    }

    // ---- helpers ----

    private static MunariumKernel Kernel() =>
        MunariumKernel.Create(
            Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}"),
            "munarium-evidence",
            MunariumShapeBundles.Load(MunariumApiFactory.ShapesDirectory));

    /// <summary>A contract-valid manifest and the canonical bytes it describes: a header and one row.</summary>
    private static (EvidenceManifest Manifest, byte[] Bytes) Sealed()
    {
        var bytes = Encoding.UTF8.GetBytes("vendor_id,status\nv-1,approved\n");

        var manifest = new EvidenceManifest
        {
            ContractVersion = EvidenceContract.Version,
            Canon = EvidenceContract.Canon,
            Tenant = MunariumKernel.Tenant,
            Kind = EvidenceKind.Table,
            LogicalResultHash = ArtifactContent.Hash("logical:vendor-status"),
            ArtifactHash = ArtifactContent.Hash(bytes),
            BytesLength = bytes.Length,
            MediaType = EvidenceContract.MediaTypeCsv,
            Source = new SourceRef("src-1", 3, "postgres"),
            Versions = new Versions { Policy = "policy-1" },
            Schema = new EvidenceSchema(
            [
                new EvidenceColumn { Id = "col-1", Name = "vendor_id", Type = ColumnType.Text, Key = true },
                new EvidenceColumn { Id = "col-2", Name = "status", Type = ColumnType.Text },
            ]),
            Identity = new EvidenceIdentity(RowIdRule.Keys),
            Completeness = new Completeness { Truncated = false },
            SnapshotVector = [new SnapshotMarker { SourceId = "src-1", ReplayLevel = "source_time_travel" }],
            Execution = new Execution { StartedAt = "2026-09-17T00:00:00Z", EndedAt = "2026-09-17T00:00:01Z" },
            AuthorizationClass = new AuthorizationClass { AccessLevel = 3 },
        };

        return (manifest, bytes);
    }

    private static WireSealResponse Sealed(WireSealResult outcome) => outcome switch
    {
        WireSealResponse response => response,
        _ => throw new InvalidOperationException($"the seal was refused: {Refusal(outcome)}"),
    };

    private static EvidenceManifest Readable(WireEvidenceManifestResult outcome) => outcome switch
    {
        EvidenceManifest manifest => manifest,
        _ => throw new InvalidOperationException($"the manifest did not resolve: {Refusal(outcome)}"),
    };

    private static WireEvidenceRows Rows(WireEvidenceRowsResult outcome) => outcome switch
    {
        WireEvidenceRows rows => rows,
        _ => throw new InvalidOperationException($"the rows did not resolve: {Refusal(outcome)}"),
    };

    private static WireEvidenceCommit Committed(WireEvidenceCommitResult outcome) => outcome switch
    {
        WireEvidenceCommit committed => committed,
        _ => throw new InvalidOperationException($"the commit was refused: {Refusal(outcome)}"),
    };

    private static WireEvidencePurge Purged(WireEvidencePurgeResult outcome) => outcome switch
    {
        WireEvidencePurge purged => purged,
        _ => throw new InvalidOperationException($"the purge was refused: {Refusal(outcome)}"),
    };

    private static WireEvidenceAccessList Accesses(WireEvidenceAccessResult outcome) => outcome switch
    {
        WireEvidenceAccessList accesses => accesses,
        _ => throw new InvalidOperationException($"the audit was refused: {Refusal(outcome)}"),
    };

    private static WireProblem Problem(WireSealResult outcome) => outcome switch
    {
        WireProblem problem => problem,
        _ => throw new InvalidOperationException("the seal was not refused"),
    };

    private static WireProblem Problem(WireEvidenceManifestResult outcome) => outcome switch
    {
        WireProblem problem => problem,
        _ => throw new InvalidOperationException("the manifest resolved"),
    };

    private static WireProblem Problem(WireEvidenceRowsResult outcome) => outcome switch
    {
        WireProblem problem => problem,
        _ => throw new InvalidOperationException("the rows resolved"),
    };

    private static WireProblem Problem(WireEvidenceCommitResult outcome) => outcome switch
    {
        WireProblem problem => problem,
        _ => throw new InvalidOperationException("the commit went through"),
    };

    private static WireProblem Problem(WireEvidencePurgeResult outcome) => outcome switch
    {
        WireProblem problem => problem,
        _ => throw new InvalidOperationException("the purge went through"),
    };

    private static string Refusal(WireSealResult outcome) => outcome is WireProblem problem ? problem.Detail : "none";

    private static string Refusal(WireEvidenceManifestResult outcome) =>
        outcome is WireProblem problem ? problem.Detail : "none";

    private static string Refusal(WireEvidenceRowsResult outcome) =>
        outcome is WireProblem problem ? problem.Detail : "none";

    private static string Refusal(WireEvidenceCommitResult outcome) =>
        outcome is WireProblem problem ? problem.Detail : "none";

    private static string Refusal(WireEvidencePurgeResult outcome) =>
        outcome is WireProblem problem ? problem.Detail : "none";

    private static string Refusal(WireEvidenceAccessResult outcome) =>
        outcome is WireProblem problem ? problem.Detail : "none";
}
