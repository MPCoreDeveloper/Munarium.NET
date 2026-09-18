namespace Munarium.Core.Tests.Evidence;

using Munarium.Evidence;
using static Munarium.Core.Tests.Support.ManifestFixture;

/// <summary>
/// Tests for writing a manifest down and reading it back. What an artifact proves about its bytes has to survive the
/// round trip unchanged, and an absent part has to come back absent rather than as a zero: the manifest's rule is that
/// absent means "not applicable to this kind", so a codec that filled the gap with a default would turn that into
/// "unknown".
/// </summary>
public class EvidenceManifestCodecTests
{
    [Fact]
    public void EveryPartOfAManifestSurvivesTheRoundTrip()
    {
        var manifest = Manifest() with
        {
            EvidenceId = "ev-0001",
            Kind = EvidenceKind.Observations,
            Source = new SourceRef("src-1", 3, "postgres")
            {
                AdapterVersion = "2.1.0",
                Engine = "PostgreSQL 17",
                Driver = "npgsql",
            },
            Versions = new Versions
            {
                QueryContract = "query@1",
                ClaimMapping = "claims@1",
                SemanticProvider = "semantic@1",
                Render = "render@1",
                Policy = "policy-1",
                Compiler = "compiler@1",
            },
            Plan = new PlanHashes { CanonicalPlanHash = Hash('d'), BoundParametersHash = Hash('e') },
            Schema = new EvidenceSchema(
            [
                new EvidenceColumn
                {
                    Id = "col-1",
                    Name = "region",
                    Type = ColumnType.Text,
                    Key = true,
                    Nullable = true,
                    Unit = "ISO-3166",
                    Additivity = "none",
                },
                new EvidenceColumn
                {
                    Id = "col-2",
                    Name = "total",
                    Type = ColumnType.ExactDecimal,
                    Scale = 2,
                    Unit = "USD",
                    Additivity = "additive",
                    ElementType = "decimal",
                },
            ]),
            Identity = new EvidenceIdentity(RowIdRule.Position) { OrderBy = ["region", "total"], Rows = 9 },
            Completeness = new Completeness
            {
                Truncated = true,
                DeclaredMaxRows = 1000,
                RowsCovered = 120,
                RowsExcluded = 3,
                ExclusionReason = "policy",
            },
            Redaction = new Redaction { DeniedColumns = ["col-9"], Masked = true },
            SnapshotVector =
            [
                new SnapshotMarker
                {
                    SourceId = "src-1",
                    Marker = "snap-1",
                    Isolation = "repeatable read",
                    StartedAt = "2026-09-17T00:00:00Z",
                    EndedAt = "2026-09-17T00:00:01Z",
                    ReplayLevel = "source_time_travel",
                    ReplayExpiresAt = "2026-10-17T00:00:00Z",
                },
            ],
            Freshness = new Freshness
            {
                Watermark = "wm-7",
                ObservedAt = "2026-09-17T00:00:02Z",
                LagSeconds = 42,
            },
            Execution = new Execution
            {
                StartedAt = "2026-09-17T00:00:00Z",
                EndedAt = "2026-09-17T00:00:01Z",
                EffectivePrincipal = "role-a",
                StatementId = "stmt-1",
            },
            AuthorizationClass = new AuthorizationClass
            {
                Name = "internal",
                AccessLevel = 3,
                Compartments = ["eu", "finance"],
            },
            Retention = new Retention
            {
                ExpiresAt = "2026-10-17T00:00:00Z",
                LegalHold = true,
                PurgedAt = "2027-01-01T00:00:00Z",
            },
        };

        var read = EvidenceManifestCodec.FromJson(EvidenceManifestCodec.ToJson(manifest), "test manifest");

        Assert.Equal(manifest.ContractVersion, read.ContractVersion);
        Assert.Equal(manifest.Canon, read.Canon);
        Assert.Equal(manifest.EvidenceId, read.EvidenceId);
        Assert.Equal(manifest.Tenant, read.Tenant);
        Assert.Equal(manifest.Kind, read.Kind);
        Assert.Equal(manifest.LogicalResultHash, read.LogicalResultHash);
        Assert.Equal(manifest.ArtifactHash, read.ArtifactHash);
        Assert.Equal(manifest.BytesLength, read.BytesLength);
        Assert.Equal(manifest.MediaType, read.MediaType);
        Assert.Equal(manifest.Source, read.Source);
        Assert.Equal(manifest.Versions, read.Versions);
        Assert.Equal(manifest.Plan, read.Plan);
        Assert.Equal(manifest.Schema.Columns, read.Schema.Columns);
        Assert.Equal(manifest.Identity.RowIdRule, read.Identity.RowIdRule);
        Assert.Equal(manifest.Identity.OrderBy, read.Identity.OrderBy);
        Assert.Equal(manifest.Identity.Rows, read.Identity.Rows);
        Assert.Equal(manifest.Completeness, read.Completeness);
        Assert.Equal(manifest.Redaction?.DeniedColumns, read.Redaction?.DeniedColumns);
        Assert.Equal(manifest.Redaction?.Masked, read.Redaction?.Masked);
        Assert.Equal(manifest.SnapshotVector, read.SnapshotVector);
        Assert.Equal(manifest.Freshness, read.Freshness);
        Assert.Equal(manifest.Execution, read.Execution);
        Assert.Equal(manifest.AuthorizationClass.Name, read.AuthorizationClass.Name);
        Assert.Equal(manifest.AuthorizationClass.AccessLevel, read.AuthorizationClass.AccessLevel);
        Assert.Equal(manifest.AuthorizationClass.Compartments, read.AuthorizationClass.Compartments);
        Assert.Equal(manifest.Retention, read.Retention);

        // The identity a reader is handed first, and the one a store looks artifacts up by: neither may move.
        Assert.Equal(manifest.ComputeDomainKey(), read.ComputeDomainKey());
        read.Validate();
    }

    /// <summary>
    /// A part that is not applicable to this kind comes back as nothing rather than as a zero, so a reader can still tell
    /// "not applicable" from "the producer said zero".
    /// </summary>
    [Fact]
    public void AManifestWithNoOptionalPartsComesBackWithNone()
    {
        var read = EvidenceManifestCodec.FromJson(EvidenceManifestCodec.ToJson(Manifest()), "test manifest");

        Assert.Null(read.EvidenceId);
        Assert.Null(read.Plan);
        Assert.Null(read.Redaction);
        Assert.Null(read.Freshness);
        Assert.Null(read.Retention);
        Assert.Equal(12, read.BytesLength);
        Assert.Null(read.Identity.Rows);
        Assert.Null(read.Completeness.DeclaredMaxRows);
        Assert.Null(read.Source.AdapterVersion);
        Assert.Null(read.Source.Engine);
        Assert.Null(read.AuthorizationClass.Name);
        Assert.Equal(RowIdRule.Keys, read.Identity.RowIdRule);
    }

    /// <summary>
    /// A field written by a later build is ignored rather than fatal: a row written yesterday has to stay readable, or
    /// every deployment would have to migrate its history to read its own past.
    /// </summary>
    [Fact]
    public void AFieldThatIsNotKnownIsIgnored()
    {
        var json = EvidenceManifestCodec.ToJson(Manifest()).Replace(
            "\"tenant\":",
            "\"something_new\": {\"a\": 1}, \"tenant\":",
            StringComparison.Ordinal);

        var read = EvidenceManifestCodec.FromJson(json, "test manifest");

        Assert.Equal("demo", read.Tenant);
    }

    [Fact]
    public void AManifestMissingWhatItNeedsIsRefused()
    {
        var json = EvidenceManifestCodec.ToJson(Manifest()).Replace(
            "\"tenant\":\"demo\",",
            string.Empty,
            StringComparison.Ordinal);

        var refusal = Assert.Throws<FormatException>(() => EvidenceManifestCodec.FromJson(json, "test manifest"));

        Assert.Contains("tenant", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The kind, the row-id rule and the column types are closed vocabularies: a value this server does not know is
    /// refused at the read rather than guessed at, because a reader that guesses cannot say what it is holding.
    /// </summary>
    [Fact]
    public void AValueTheServerDoesNotKnowIsRefused()
    {
        Assert.Contains(
            "kind",
            Assert.Throws<FormatException>(() => ReadAfter("kind\":\"table", "kind\":\"matrix")).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "row-id",
            Assert.Throws<FormatException>(() => ReadAfter("row_id_rule\":\"keys", "row_id_rule\":\"ordinal")).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "column type",
            Assert.Throws<FormatException>(() => ReadAfter("type\":\"decimal", "type\":\"money")).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EvidenceKind.Table, "table")]
    [InlineData(EvidenceKind.Count, "count")]
    [InlineData(EvidenceKind.Observations, "observations")]
    public void TheKindIsSpelledExplicitly(EvidenceKind kind, string spelling)
    {
        var json = EvidenceManifestCodec.ToJson(Manifest() with { Kind = kind });

        Assert.Contains($"\"{spelling}\"", json, StringComparison.Ordinal);
        Assert.Equal(kind, EvidenceManifestCodec.FromJson(json, "test manifest").Kind);
    }

    /// <summary>
    /// The names are the contract's, not this port's: they are the same names the original's JSON schema declares, and
    /// they travel on both transports and into the stored row. A name that drifted here would be a field no
    /// implementation of the contract could read.
    /// </summary>
    [Fact]
    public void TheNamesAreTheContracts()
    {
        var json = EvidenceManifestCodec.ToJson(
            Manifest() with { Retention = new Retention { ExpiresAt = "2026-10-01T00:00:00Z", LegalHold = true } });

        Assert.Contains("\"contract_version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"logical_result_hash\"", json, StringComparison.Ordinal);
        Assert.Contains("\"artifact_hash\"", json, StringComparison.Ordinal);

        // `bytes_len`, not `bytes_length`: the contract spells it that way, and so does the column it is lifted into.
        Assert.Contains("\"bytes_len\":12", json, StringComparison.Ordinal);
        Assert.Contains("\"media_type\"", json, StringComparison.Ordinal);
        Assert.Contains("\"row_id_rule\"", json, StringComparison.Ordinal);
        Assert.Contains("\"replay_level\"", json, StringComparison.Ordinal);
        Assert.Contains("\"access_level\"", json, StringComparison.Ordinal);
        Assert.Contains("\"legal_hold\"", json, StringComparison.Ordinal);
        Assert.Contains("\"snapshot_vector\"", json, StringComparison.Ordinal);
    }

    private static EvidenceManifest ReadAfter(string replace, string with) =>
        EvidenceManifestCodec.FromJson(
            EvidenceManifestCodec.ToJson(Manifest()).Replace(replace, with, StringComparison.Ordinal),
            "test manifest");
}
