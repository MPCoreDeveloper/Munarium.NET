namespace Munarium.Core.Tests.Governance;

using System.Text;
using System.Text.Json.Nodes;
using Munarium.Claims;
using Munarium.Governance;

/// <summary>
/// Tests for the canonical findings encoding. The specific bytes matter: the payload is what a later reader
/// gets back as the verdict, so a change here is a contract change.
/// </summary>
public class FindingCodecTests
{
    [Fact]
    public void AFindingsBatchRoundTripsThroughItsPayload()
    {
        GateFinding[] findings =
        [
            new()
            {
                RuleId = "gate.ledger-conflict",
                Severity = Severity.Block,
                Message = "claim 'service.api_version=v2' conflicts with accepted canon",
                ScopePath = "release.notes",
                Detail = new JsonObject
                {
                    ["claim_key"] = "service.api_version",
                    ["canon_seq"] = 1,
                    ["ratio"] = 0.75,
                    ["nested"] = new JsonObject { ["value"] = "a=b\nc" },
                },
            },
            new()
            {
                RuleId = "gate.meta-leakage",
                Severity = Severity.Warn,
                Message = "output contains meta-leakage marker 'as an ai'",
                Detail = new JsonObject { ["marker"] = "as an ai" },
            },
        ];

        var decoded = FindingCodec.Decode(FindingCodec.Encode(findings));

        Assert.Equal(2, decoded.Count);
        Assert.Equal(findings[0].RuleId, decoded[0].RuleId);
        Assert.Equal(Severity.Block, decoded[0].Severity);
        Assert.Equal(findings[0].Message, decoded[0].Message);
        Assert.Equal("release.notes", decoded[0].ScopePath);

        // The structured detail keeps its shape, including a nested object and a number.
        Assert.Equal(findings[0].Detail!.ToJsonString(), decoded[0].Detail!.ToJsonString());
        Assert.Equal("as an ai", decoded[1].Detail!["marker"]!.GetValue<string>());

        // A finding that named no scope comes back naming none.
        Assert.Null(decoded[1].ScopePath);
    }

    [Fact]
    public void AnEmptyBatchRoundTrips() => Assert.Empty(FindingCodec.Decode(FindingCodec.Encode([])));

    [Fact]
    public void ASeverityIsPinnedByItsNumericValue()
    {
        var payload = Encoding.UTF8.GetString(
            FindingCodec.Encode([new GateFinding { RuleId = "gate.test", Severity = Severity.Warn, Message = "m" }]));

        Assert.Contains("\"severity\":1", payload, StringComparison.Ordinal);
        Assert.Contains("\"rule_id\":\"gate.test\"", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// A verdict filed under the wrong severity is worse than a payload that fails to read, so an unknown
    /// value is refused rather than mapped onto something plausible.
    /// </summary>
    [Fact]
    public void AnUnknownSeverityIsRefused() =>
        Assert.Throws<FormatException>(() =>
            FindingCodec.Decode("""[{"rule_id":"gate.test","severity":7,"message":"m"}]"""u8.ToArray()));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"rule_id":"gate.test"}""")]
    [InlineData("""[{"severity":1,"message":"m"}]""")]
    [InlineData("""["gate.test"]""")]
    public void AMalformedPayloadIsRefused(string payload) =>
        Assert.Throws<FormatException>(() => FindingCodec.Decode(Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public void OnlyTheFindingsEventTypeIsRecognised()
    {
        Assert.True(FindingCodec.IsFindingsEvent(FindingCodec.FindingsEventType));
        Assert.False(FindingCodec.IsFindingsEvent("fact.asserted"));
        Assert.False(FindingCodec.IsFindingsEvent("gate.ledger-conflict"));
    }
}
