namespace Munarium.Core.Tests.Governance;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Governance.Gates;

/// <summary>
/// Tests for the two gates that judge the produced text rather than a proposal: leaked markers and
/// repetition.
/// </summary>
public class TextGateTests
{
    [Theory]
    [InlineData("As an AI, that request is out of scope for me.")]
    [InlineData("I'm sorry, but I cannot do that.")]
    [InlineData("Lorem ipsum dolor sit amet")]
    [InlineData("Then say: [insert name here]")]
    public void ALeakedMarkerInTheTextIsWarnedAbout(string text)
    {
        var finding = Assert.Single(MetaLeakage.Evaluate(ClaimFixture.Snapshot(), new Candidate { Text = text }));

        Assert.Equal(MetaLeakage.RuleId, finding.RuleId);
        Assert.Equal(Severity.Warn, finding.Severity);
        Assert.False(string.IsNullOrEmpty(finding.Detail!["marker"]!.GetValue<string>()));
    }

    /// <summary>Every marker present is reported, not just the first one found.</summary>
    [Fact]
    public void EachLeakedMarkerIsReported() =>
        Assert.Equal(
            2,
            MetaLeakage.Evaluate(
                ClaimFixture.Snapshot(),
                new Candidate { Text = "As an AI, I cannot assist with that request." }).Count);

    [Fact]
    public void CleanTextIsNotWarnedAbout() =>
        Assert.Empty(MetaLeakage.Evaluate(
            ClaimFixture.Snapshot(),
            new Candidate { Text = "The harbour was quiet until the second bell." }));

    /// <summary>
    /// A meta-leakage finding names no claim: it is about the unit of text, so nothing can be
    /// attributed to a proposal by reading it.
    /// </summary>
    [Fact]
    public void AMetaLeakageFindingNamesNoClaim() =>
        Assert.Null(MetaLeakage.Evaluate(ClaimFixture.Snapshot(), new Candidate { Text = "As an AI" })[0].ClaimKey);

    [Fact]
    public void RepetitionOfAPreviousUnitIsWarnedAbout()
    {
        var findings = LexicalSimilarity.Evaluate(
            ClaimFixture.Snapshot(),
            new Candidate
            {
                Text = "The chapter opens with rain on the harbor and the bell ringing once.",
                PreviousTexts = ["The chapter opens with rain on the harbor and the bell ringing twice."],
            });

        var finding = Assert.Single(findings);
        Assert.Equal(LexicalSimilarity.RuleId, finding.RuleId);
        Assert.Equal(0, finding.Detail!["previous_index"]!.GetValue<int>());
        Assert.True(finding.Detail["ratio"]!.GetValue<double>() >= LexicalSimilarity.Threshold);
    }

    [Fact]
    public void ThePreviousUnitThatIsRepeatedIsTheOneNamed()
    {
        var first = "The harbour was quiet until the second bell rang out across the water.";
        var second = "A convoy of water cans was counted in silence somewhere in the desert.";

        var findings = LexicalSimilarity.Evaluate(
            ClaimFixture.Snapshot(),
            new Candidate { Text = first, PreviousTexts = [second, first] });

        Assert.Equal(1, Assert.Single(findings).Detail!["previous_index"]!.GetValue<int>());
    }

    [Fact]
    public void TextThatIsNotARepetitionIsNotWarnedAbout() =>
        Assert.Empty(LexicalSimilarity.Evaluate(
            ClaimFixture.Snapshot(),
            new Candidate
            {
                Text = "Elsewhere the desert convoy counted its water cans in silence.",
                PreviousTexts = ["The chapter opens with rain on the harbor and the bell ringing twice."],
            }));

    [Fact]
    public void AnEmptyTextIsNeverARepetition() =>
        Assert.Empty(LexicalSimilarity.Evaluate(
            ClaimFixture.Snapshot(),
            new Candidate { Text = "   ", PreviousTexts = ["anything"] }));
}
