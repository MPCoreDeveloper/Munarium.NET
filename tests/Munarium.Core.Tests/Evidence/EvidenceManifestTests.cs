namespace Munarium.Core.Tests.Evidence;

using Munarium.Evidence;

/// <summary>
/// Tests for the evidence contract's identity and access rules: the domain idempotency tuple, who may read an
/// artifact, the shape of a hash, and the contract spellings.
/// </summary>
public class EvidenceManifestTests
{
    /// <summary>
    /// The reason the logical result and the bytes have separate hashes: re-serializing one logical result must
    /// not mint a second artifact.
    /// </summary>
    [Fact]
    public void TheDomainKeyIgnoresTheArtifactHash() =>
        Assert.Equal(Manifest().ComputeDomainKey(), (Manifest() with { ArtifactHash = Hash('a') }).ComputeDomainKey());

    [Fact]
    public void TheDomainKeyMovesWithTenantAndPolicy()
    {
        Assert.NotEqual(
            Manifest().ComputeDomainKey(),
            (Manifest() with { Tenant = "other-tenant" }).ComputeDomainKey());

        Assert.NotEqual(
            Manifest().ComputeDomainKey(),
            (Manifest() with { Versions = new Versions { Policy = "policy-2" } }).ComputeDomainKey());
    }

    [Fact]
    public void TheDomainKeyMovesWithTheAuthorizationClass()
    {
        Assert.NotEqual(Manifest().ComputeDomainKey(), (Manifest() with { AuthorizationClass = Class(5) }).ComputeDomainKey());
        Assert.NotEqual(
            Manifest().ComputeDomainKey(),
            (Manifest() with { AuthorizationClass = Class(3, "eu", "finance") }).ComputeDomainKey());
    }

    /// <summary>
    /// The bug the unit separator closes: nothing validates a compartment tag against commas, so joined by a
    /// comma <c>["a,b"]</c> and <c>["a", "b"]</c> were one key - and a lookup could have replayed an artifact
    /// sealed under a different authorization class.
    /// </summary>
    [Fact]
    public void OneCompartmentHoldingACommaIsNotTwoCompartments() =>
        Assert.NotEqual(
            (Manifest() with { AuthorizationClass = Class(3, "a,b") }).ComputeDomainKey(),
            (Manifest() with { AuthorizationClass = Class(3, "a", "b") }).ComputeDomainKey());

    [Fact]
    public void TheDomainKeyIsStableAgainstCompartmentOrderAndIsAnIdempotencyTuple()
    {
        Assert.Equal(
            (Manifest() with { AuthorizationClass = Class(3, "eu", "finance") }).ComputeDomainKey(),
            (Manifest() with { AuthorizationClass = Class(3, "finance", "eu") }).ComputeDomainKey());

        Assert.StartsWith("dk-", Manifest().ComputeDomainKey(), StringComparison.Ordinal);
    }

    /// <summary>An unrestricted reader clears the compartment gate and never the level gate.</summary>
    [Fact]
    public void AnUnrestrictedReaderStillHasToClearTheLevel()
    {
        var artifact = Class(5, "eu");

        Assert.False(artifact.DominatedBy(3, compartments: null, allCompartments: true));
        Assert.True(artifact.DominatedBy(5, compartments: null, allCompartments: true));
    }

    [Fact]
    public void AReaderHasToHoldEveryCompartment()
    {
        var artifact = Class(3, "eu", "finance");

        Assert.True(artifact.DominatedBy(3, ["eu", "finance", "extra"], allCompartments: false));
        Assert.False(artifact.DominatedBy(3, ["eu"], allCompartments: false));
        Assert.False(artifact.DominatedBy(2, ["eu", "finance"], allCompartments: false));
        Assert.False(artifact.DominatedBy(3, compartments: null, allCompartments: false));
    }

    [Fact]
    public void AHashIsLowercaseSha256AndNothingElse()
    {
        Assert.True(EvidenceContract.IsHash(Hash('0')));
        Assert.False(EvidenceContract.IsHash(Hash('A')));
        Assert.False(EvidenceContract.IsHash("sha256:abc"));
        Assert.False(EvidenceContract.IsHash("md5:" + new string('0', 64)));
        Assert.False(EvidenceContract.IsHash(null));
        Assert.False(EvidenceContract.IsHash(new string('0', 64)));
    }

    [Fact]
    public void OnlyTheContractMajorIsCompared()
    {
        Assert.True(EvidenceContract.MajorMatches("1.9.9"));
        Assert.True(EvidenceContract.MajorMatches(" 1 "));
        Assert.False(EvidenceContract.MajorMatches("2.0.0"));
        Assert.False(EvidenceContract.MajorMatches(null));
        Assert.Equal("1", EvidenceContract.Major("1.0.0"));
    }

    /// <summary>The contract's spellings are pinned here, so renaming a member cannot quietly move the wire.</summary>
    [Theory]
    [InlineData(ColumnType.Bool, "bool")]
    [InlineData(ColumnType.WholeNumber, "int64")]
    [InlineData(ColumnType.ExactDecimal, "decimal")]
    [InlineData(ColumnType.RealNumber, "float64")]
    [InlineData(ColumnType.Text, "string")]
    [InlineData(ColumnType.TimestampTz, "timestamp_tz")]
    [InlineData(ColumnType.TimestampNaive, "timestamp_naive")]
    [InlineData(ColumnType.Array, "array")]
    public void TheColumnTypeSpellingsAreTheContracts(ColumnType type, string name) =>
        Assert.Equal(name, type.ToContractName());

    [Fact]
    public void EveryColumnTypeRoundTripsThroughItsContractName() =>
        Assert.All(
            Enum.GetValues<ColumnType>(),
            type => Assert.Equal(type, ColumnTypeNames.ParseType(type.ToContractName())));

    [Fact]
    public void AnUnknownContractNameIsNotGuessed() => Assert.Null(ColumnTypeNames.ParseType("money"));

    [Fact]
    public void TheLifecycleNamesRoundTrip()
    {
        Assert.Equal("pending", EvidenceState.Pending.ToWireName());
        Assert.Equal("committed", EvidenceState.Committed.ToWireName());
        Assert.Equal("purged", EvidenceState.Purged.ToWireName());

        Assert.Equal(EvidenceState.Purged, EvidenceStateNames.ParseState("purged"));
        Assert.Null(EvidenceStateNames.ParseState("deleted"));
    }

    private static string Hash(char fill) => "sha256:" + new string(fill, 64);

    private static AuthorizationClass Class(int level, params string[] compartments) =>
        new() { AccessLevel = level, Compartments = compartments };

    private static EvidenceManifest Manifest() => new()
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
        Schema = new EvidenceSchema([new EvidenceColumn { Id = "col-1", Name = "total", Type = ColumnType.ExactDecimal }]),
        Identity = new EvidenceIdentity(RowIdRule.Keys),
        Completeness = new Completeness { Truncated = false },
        SnapshotVector = [new SnapshotMarker { SourceId = "src-1", ReplayLevel = "source_time_travel" }],
        Execution = new Execution { StartedAt = "2026-09-17T00:00:00Z", EndedAt = "2026-09-17T00:00:01Z" },
        AuthorizationClass = Class(3),
    };
}

