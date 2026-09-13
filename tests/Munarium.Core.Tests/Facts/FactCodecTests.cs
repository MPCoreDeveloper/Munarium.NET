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

        Assert.Equal(
            "claimId=claim-1\nlineage=vendor/north\nstatement=stable\nactor=compliance\ngate=\nreason=\n",
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
            Lineage = "vendor\\north",
            Statement = "line one\nline two",
            Actor = "a\\b",
            Gate = "policy",
            Reason = "rule 1 = rule 2\r\nand more",
        };

        Assert.Equal(fact, FactCodec.Decode(FactCodec.Encode(fact)));
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
        Lineage = "vendor/north",
        Statement = statement,
        Actor = "compliance",
        Gate = string.Empty,
        Reason = string.Empty,
    };
}
