namespace Munarium.Core.Tests.Support;

using Munarium.Evidence;

/// <summary>
/// A valid manifest, and the small helpers a test needs to vary one field of it.
/// </summary>
public static class ManifestFixture
{
    /// <summary>Builds a well-formed hash from one repeated lowercase hex digit.</summary>
    /// <param name="fill">The digit to repeat.</param>
    /// <returns>The hash.</returns>
    public static string Hash(char fill) => "sha256:" + new string(fill, 64);

    /// <summary>Builds an authorization class.</summary>
    /// <param name="level">The access level.</param>
    /// <param name="compartments">The compartments.</param>
    /// <returns>The class.</returns>
    public static AuthorizationClass Class(int level, params string[] compartments) =>
        new() { AccessLevel = level, Compartments = compartments };

    /// <summary>Builds a manifest that satisfies every contract rule.</summary>
    /// <returns>The manifest.</returns>
    public static EvidenceManifest Manifest() => new()
    {
        ContractVersion = EvidenceContract.Version,
        Canon = EvidenceContract.Canon,
        Tenant = "demo",
        Kind = EvidenceKind.Table,
        LogicalResultHash = Hash('b'),
        ArtifactHash = Hash('c'),
        BytesLength = 12,
        MediaType = EvidenceContract.MediaTypeCsv,
        Source = new SourceRef("src-1", 3, "postgres"),
        Versions = new Versions { Policy = "policy-1" },
        Schema = new EvidenceSchema(
        [
            new EvidenceColumn { Id = "col-1", Name = "region", Type = ColumnType.Text, Key = true },
            new EvidenceColumn { Id = "col-2", Name = "total", Type = ColumnType.ExactDecimal },
        ]),
        Identity = new EvidenceIdentity(RowIdRule.Keys),
        Completeness = new Completeness { Truncated = false },
        SnapshotVector = [new SnapshotMarker { SourceId = "src-1", ReplayLevel = "source_time_travel" }],
        Execution = new Execution { StartedAt = "2026-09-17T00:00:00Z", EndedAt = "2026-09-17T00:00:01Z" },
        AuthorizationClass = Class(3),
    };
}
