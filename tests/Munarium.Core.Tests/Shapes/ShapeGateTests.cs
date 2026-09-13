namespace Munarium.Core.Tests.Shapes;

using Munarium.Commands;
using Munarium.Core.Tests.Support;
using Munarium.Governance;
using Munarium.Shapes;

/// <summary>
/// Tests for the gate that turns a shape's schema into a governance verdict.
/// </summary>
public class ShapeGateTests
{
    [Fact]
    public async Task AConformingBodyIsPermitted() =>
        Assert.Equal("permitted", Describe(await JudgeAsync(VendorShape.Body("north", "pending"))));

    [Fact]
    public async Task AViolationIsBlockedWithTheValidatorsReason() =>
        Assert.Equal(
            "blocked:shape:$: required property 'status' is missing.",
            Describe(await JudgeAsync("""{"vendor_id":"north"}""")));

    [Fact]
    public async Task ABodyThatIsNotJsonIsBlockedRatherThanThrowing() =>
        Assert.Equal(
            "blocked:shape:$: the body is not valid JSON (line 0, position 1).",
            Describe(await JudgeAsync("not json at all")));

    [Fact]
    public async Task AnUnknownShapeIsBlockedRatherThanThrowing()
    {
        var gate = new ShapeGate(VendorShape.Registry());

        var verdict = await gate.EvaluateAsync(Claim(VendorShape.Body("north")) with { Shape = "patent" });

        Assert.Equal("blocked:shape:no shape named 'patent' is registered", Describe(verdict));
    }

    [Fact]
    public void TheGateRefusesToBeBuiltWithoutARegistry() =>
        Assert.Throws<ArgumentNullException>(() => new ShapeGate(null!));

    private static async Task<ClaimVerdict> JudgeAsync(string body)
    {
        var gate = new ShapeGate(VendorShape.Registry());

        return await gate.EvaluateAsync(Claim(body));
    }

    private static RecordClaimCommand Claim(string body) => new()
    {
        Stream = "claims/1",
        ClaimId = "claim-1",
        Shape = VendorShape.Name,
        Body = body,
        Statement = "the supplier is north",
        Actor = "tester",
    };

    private static string Describe(ClaimVerdict verdict) => verdict switch
    {
        Permitted => "permitted",
        Blocked blocked => $"blocked:{blocked.Gate}:{blocked.Reason}",
    };
}
