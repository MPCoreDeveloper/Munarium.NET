namespace Munarium.Core.Tests.Claims;

using Munarium.Claims;

/// <summary>
/// Tests for the canonical <c>subject.key=value</c> form every gate parses.
/// </summary>
public class ClaimTextTests
{
    [Fact]
    public void NormalizeRoundTripsThroughSplit()
    {
        var normalized = ClaimText.Normalize("hero", "eye_color", "green");

        Assert.Equal("hero.eye_color=green", normalized);
        Assert.Equal(("hero", "eye_color", "green"), ClaimText.Split(normalized));
    }

    [Fact]
    public void NormalizeTrimsEveryPart() =>
        Assert.Equal("hero.eye_color=green", ClaimText.Normalize(" hero ", " eye_color ", " green "));

    [Fact]
    public void SplitToleratesFreeText()
    {
        Assert.Equal((string.Empty, string.Empty, "no equals here"), ClaimText.Split("no equals here"));
        Assert.Equal((string.Empty, "nodot", "x"), ClaimText.Split("nodot=x"));
    }

    /// <summary>
    /// A composite key splits at the last dot: keys are dotted identifiers, so the first-dot reading
    /// would fold part of the key into the subject.
    /// </summary>
    [Fact]
    public void SplitBreaksCompositeKeysAtTheLastDot() =>
        Assert.Equal(("release.date", "v4", "2026"), ClaimText.Split("release.date.v4=2026"));

    [Theory]
    [InlineData("green", "green", true)]
    [InlineData("Green", "green", true)]
    [InlineData("  green  ", "green", true)]
    [InlineData("north  gate", "north gate", true)]
    [InlineData("green", "blue", false)]
    [InlineData("", "", true)]
    public void ValuesCompareCaseAndWhitespaceInsensitively(string left, string right, bool equivalent) =>
        Assert.Equal(equivalent, ClaimText.ValuesEquivalent(left, right));
}
