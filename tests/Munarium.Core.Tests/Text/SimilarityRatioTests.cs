namespace Munarium.Core.Tests.Text;

using Munarium.Text;

/// <summary>
/// Tests for the Ratcliff-Obershelp ratio, which the repetition gate and the drift engine both key
/// off - so the port has to agree with the original on the algorithm, not merely on the idea.
/// </summary>
public class SimilarityRatioTests
{
    [Fact]
    public void IdenticalTextScoresOne() => Assert.Equal(1.0, SimilarityRatio.Compare("abc def", "abc def"), 1e-9);

    [Fact]
    public void TextWithNothingInCommonScoresZero() => Assert.Equal(0.0, SimilarityRatio.Compare("aaa", "zzz"));

    [Fact]
    public void TwoEmptyStringsScoreOne() => Assert.Equal(1.0, SimilarityRatio.Compare(string.Empty, string.Empty));

    [Fact]
    public void TheComparisonIsCaseInsensitive() =>
        Assert.Equal(1.0, SimilarityRatio.Compare("Hello World", "hello world"), 1e-9);

    [Fact]
    public void NearDuplicateProseCrossesTheRepetitionThreshold() =>
        Assert.True(SimilarityRatio.Compare(
            "The chapter opens with rain on the harbor and the bell ringing twice.",
            "The chapter opens with rain on the harbor and the bell ringing once.") >= 0.9);

    [Fact]
    public void DifferentProseStaysUnderTheThreshold() =>
        Assert.True(SimilarityRatio.Compare(
            "The chapter opens with rain on the harbor and the bell ringing twice.",
            "Elsewhere the desert convoy counted its water cans in silence.") < 0.9);

    /// <summary>
    /// The ratio is symmetric and bounded, which is what lets a threshold mean one thing rather than
    /// two depending on which text the caller happened to pass first.
    /// </summary>
    [Theory]
    [InlineData("the bell rang twice", "the bell rang once")]
    [InlineData("north gate", "south gate")]
    [InlineData("a", "abc")]
    public void TheScoreIsSymmetricAndWithinTheUnitInterval(string first, string second)
    {
        var forward = SimilarityRatio.Compare(first, second);

        Assert.Equal(forward, SimilarityRatio.Compare(second, first), 1e-9);
        Assert.InRange(forward, 0.0, 1.0);
    }

    /// <summary>
    /// An astral character counts once, not twice: the comparison runs over Unicode scalar values, so a
    /// code-unit reading cannot score one character as two.
    /// </summary>
    [Fact]
    public void AnAstralCharacterCountsAsOneCharacter() =>
        Assert.Equal(1.0, SimilarityRatio.Compare("\U0001F600\U0001F600", "\U0001F600\U0001F600"), 1e-9);
}
