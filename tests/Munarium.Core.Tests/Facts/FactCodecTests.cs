namespace Munarium.Core.Tests.Facts;

using System.Text;
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
        // reason this expectation is written out rather than derived.
        Assert.Equal(
            "claimId=claim-1\n"
                + "versionId=vendor-eu\n"
                + "claimType=3\n"
                + "lineage=vendor/north\n"
                + "body={\"vendor_id\":\"north\"}\n"
                + "statement=stable\n"
                + "actor=compliance\n"
                + "gate=\n"
                + "reason=\n",
            Encoding.UTF8.GetString(FactCodec.Encode(fact)));
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
            Body = """{"note":"a=b"}""",
            Statement = "line one\nline two",
            Actor = "a\\b",
            Gate = "policy",
            Reason = "rule 1 = rule 2\r\nand more",
        };

        Assert.Equal(fact, FactCodec.Decode(FactCodec.Encode(fact)));
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
