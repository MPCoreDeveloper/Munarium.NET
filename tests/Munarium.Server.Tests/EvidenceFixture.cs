namespace Munarium.Server.Tests;

using System.Text;
using Munarium.Evidence;

/// <summary>
/// The manifest and bytes a test seals, shared by the plane's own tests and the surface's so the two cannot seal
/// different things and call them the same.
/// </summary>
internal static class EvidenceFixture
{
    /// <summary>Builds a contract-valid manifest and the canonical bytes it describes: a header and one row.</summary>
    /// <param name="payload">The row's second cell, which makes each artifact distinct.</param>
    /// <returns>The manifest and the bytes.</returns>
    public static (EvidenceManifest Manifest, byte[] Bytes) Artifact(string payload = "approved")
    {
        var bytes = Encoding.UTF8.GetBytes($"vendor_id,status\nv-1,{payload}\n");

        var manifest = new EvidenceManifest
        {
            ContractVersion = EvidenceContract.Version,
            Canon = EvidenceContract.Canon,
            Tenant = MunariumKernel.Tenant,
            Kind = EvidenceKind.Table,
            LogicalResultHash = ArtifactContent.Hash($"logical:vendor-status:{payload}"),
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
}
