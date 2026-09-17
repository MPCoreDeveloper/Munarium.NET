namespace Munarium.Core.Tests.Facts;

using System.Text;
using Munarium.Claims;
using Munarium.Facts;

/// <summary>
/// Tests for the canonical fact encoding. The specific bytes matter: a slice digest is computed
/// over them, so a change here is a contract change.
/// </summary>
public class FactCodecTests
{
    [Fact]
    public void EncodingIsStableAndPinned()
    {
        var fact = Fact("stable");

        // The field order is part of the contract: a slice digest is computed over these bytes, so adding
        // versionId, claimType and body moved every digest - which is a change a release notes, and the
        // reason this expectation is written out rather than derived. Adding the semantic triple and the
        // claim's own fields to a payload written under a shape moved them again, for the same reason.
        Assert.Equal(
            "claimId=claim-1\n"
                + "versionId=vendor-eu\n"
                + "claimType=3\n"
                + "lineage=vendor/north\n"
                + "subject=\n"
                + "key=\n"
                + "value=\n"
                + "scopePath=\n"
                + "provenance=0\n"
                + "supersedesId=\n"
                + "entityId=\n"
                + "evidence=\n"
                + "confidence=\n"
                + "shapeRef=\n"
                + "body={\"vendor_id\":\"north\"}\n"
                + "statement=stable\n"
                + "actor=compliance\n"
                + "gate=\n"
                + "reason=\n",
            Encoding.UTF8.GetString(FactCodec.Encode(fact)));
    }

    /// <summary>
    /// A claim that names its own triple is the other kind of stored fact, and the encoding carries it in
    /// full: the value, the scope, the provenance, what it supersedes, and the number a model was sure of.
    /// </summary>
    [Fact]
    public void EncodingPinsTheSemanticTriple()
    {
        var fact = new FactRecord
        {
            ClaimId = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            VersionId = "release-1",
            ClaimType = ClaimType.Correction,
            Lineage = "service.api_version",
            Subject = "service",
            Key = "api_version",
            Value = "v2",
            ScopePath = "release.notes",
            Provenance = Provenance.Backfilled,
            SupersedesId = "01ARZ3NDEKTSV4RRFFQ69G5FA0",
            EntityId = "entity-7",
            EvidenceJson = """{"doc":"rfc-1"}""",
            Confidence = 0.75,
            ShapeRef = "api@2",
            Body = string.Empty,
            Statement = string.Empty,
            Actor = string.Empty,
            Gate = string.Empty,
            Reason = string.Empty,
        };

        Assert.Equal(
            "claimId=01ARZ3NDEKTSV4RRFFQ69G5FAV\n"
                + "versionId=release-1\n"
                + "claimType=3\n"
                + "lineage=service.api_version\n"
                + "subject=service\n"
                + "key=api_version\n"
                + "value=v2\n"
                + "scopePath=release.notes\n"
                + "provenance=1\n"
                + "supersedesId=01ARZ3NDEKTSV4RRFFQ69G5FA0\n"
                + "entityId=entity-7\n"
                + "evidence={\"doc\":\"rfc-1\"}\n"
                + "confidence=0.75\n"
                + "shapeRef=api@2\n"
                + "body=\n"
                + "statement=\n"
                + "actor=\n"
                + "gate=\n"
                + "reason=\n",
            Encoding.UTF8.GetString(FactCodec.Encode(fact)));

        Assert.Equal(fact, FactCodec.Decode(FactCodec.Encode(fact)));
    }

    /// <summary>
    /// The format is additive: a payload written before a field existed decodes with that field absent
    /// rather than failing, which is the only reason a canonical form may grow without a migration.
    /// </summary>
    [Fact]
    public void APayloadWrittenBeforeAFieldExistedStillDecodes()
    {
        byte[] older = "claimId=c\nversionId=v\nclaimType=1\nlineage=l\nbody={}\nstatement=s\nactor=a\ngate=\nreason=\n"u8.ToArray();

        var fact = FactCodec.Decode(older);

        Assert.Equal(string.Empty, fact.Subject);
        Assert.Equal(string.Empty, fact.Key);
        Assert.Null(fact.ScopePath);
        Assert.Equal(Provenance.Witnessed, fact.Provenance);
        Assert.Null(fact.SupersedesId);
        Assert.Null(fact.Confidence);
        Assert.Null(fact.ShapeRef);
    }

    [Fact]
    public void EncodeThenDecodeRoundTripsEveryField()
    {
        var fact = Fact("the supplier is north");

        Assert.Equal(fact, FactCodec.Decode(FactCodec.Encode(fact)));
    }

    [Fact]
    public void ValuesWithBackslashesNewlinesAndEqualsRoundTripExactly()
    {
        var fact = new FactRecord
        {
            ClaimId = "claim=2",
            VersionId = "vendor\\eu",
            ClaimType = ClaimType.Update,
            Lineage = "vendor\\north",
            Subject = "vendor\nnorth",
            Key = "st=atus",
            Value = "app\\roved",
            ScopePath = "eu\\north",
            Provenance = Provenance.CoverageRepair,
            SupersedesId = "claim=1",
            EntityId = "entity\\7",
            EvidenceJson = """{"note":"a=b"}""",
            Confidence = 0.5,
            ShapeRef = "vendor@1\r\n",
            Body = """{"note":"a=b"}""",
            Statement = "line one\nline two",
            Actor = "a\\b",
            Gate = "policy",
            Reason = "rule 1 = rule 2\r\nand more",
        };

        Assert.Equal(fact, FactCodec.Decode(FactCodec.Encode(fact)));
    }

    [Fact]
    public void AnUnknownProvenanceOrConfidenceIsRefusedRatherThanGuessed()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            "claimId=c\nversionId=v\nclaimType=1\nlineage=l\nprovenance=99\nconfidence=lots\nbody={}\n"
                + "statement=s\nactor=a\ngate=\nreason=\n");

        Assert.Throws<FormatException>(() => FactCodec.Decode(payload));
    }

    [Fact]
    public void AnUnknownClaimTypeIsRefusedRatherThanGuessed()
    {
        byte[] payload = "claimId=c\nversionId=v\nclaimType=99\nlineage=l\nbody={}\nstatement=s\nactor=a\ngate=\nreason=\n"u8.ToArray();

        Assert.Throws<FormatException>(() => FactCodec.Decode(payload));
    }

    [Fact]
    public void DecodingAMalformedPayloadThrows()
    {
        Assert.Throws<FormatException>(() => FactCodec.Decode("not-a-field"u8));
    }

    [Fact]
    public void OnlyFactEventTypesAreRecognised()
    {
        Assert.True(FactCodec.IsFactEvent(FactCodec.AssertedEventType));
        Assert.True(FactCodec.IsFactEvent(FactCodec.DisputedEventType));
        Assert.False(FactCodec.IsFactEvent("ingest.recorded"));
    }

    private static FactRecord Fact(string statement) => new()
    {
        ClaimId = "claim-1",
        VersionId = "vendor-eu",
        ClaimType = ClaimType.Correction,
        Lineage = "vendor/north",
        Body = """{"vendor_id":"north"}""",
        Statement = statement,
        Actor = "compliance",
        Gate = string.Empty,
        Reason = string.Empty,
    };
}
