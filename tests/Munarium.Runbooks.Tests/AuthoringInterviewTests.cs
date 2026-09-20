namespace Munarium.Runbooks.Tests;

/// <summary>Tests for the authoring interview: the questions, and the order they are asked in.</summary>
/// <remarks>
/// The order is the contract, so these tests assert the order and the gating rather than the wording: sections run from
/// the decisions that cannot be revised to the ones that are a version away, and the completion section follows the
/// pattern's own arm.
/// </remarks>
public class AuthoringInterviewTests
{
    /// <summary>The sections follow the revisability order.</summary>
    [Fact]
    public void SectionsFollowTheRevisabilityOrder()
    {
        var ids = AuthoringInterview.For(pattern: null).Select(section => section.Id);

        Assert.Equal(
            ["identity", "prefix-layout", "access", "retrieval", "extraction", "lifecycle", "completion"],
            ids);
    }

    /// <summary>The completion section follows the pattern's own arm, for every pattern in the catalog.</summary>
    [Fact]
    public void TheCompletionSectionFollowsThePatternsArm()
    {
        foreach (var pattern in AuthoringCatalog.Patterns)
        {
            var asked = AuthoringInterview.For(pattern).Any(section => section.Id == "completion");

            Assert.Equal(pattern.HasCompletion, asked);
        }

        // Both directions exist in the catalog, so the loop above is not vacuous.
        Assert.Contains(AuthoringCatalog.Patterns, pattern => pattern.HasCompletion);
        Assert.Contains(AuthoringCatalog.Patterns, pattern => !pattern.HasCompletion);
    }

    /// <summary>The pattern question offers exactly what the catalog states.</summary>
    [Fact]
    public void ThePatternQuestionOffersTheCatalog()
    {
        var question = AuthoringInterview
            .For(pattern: null)
            .SelectMany(section => section.Questions)
            .Single(asked => asked.Id == "identity.pattern");

        Assert.Equal(AuthoringCatalog.Patterns.Select(pattern => pattern.Id), question.Choices);
    }

    /// <summary>A pattern whose exemplar a deployment does not carry is not offered.</summary>
    /// <remarks>
    /// The rule upstream enforces by embedding: the interview never offers a choice the catalog would then refuse. Here a
    /// deployment says what it carries, and the offered set follows from that.
    /// </remarks>
    [Fact]
    public void AnUnavailableExemplarIsNotOffered()
    {
        string[] carried = ["financial-advisory", "due-diligence"];

        var served = AuthoringCatalog.Served(carried);
        var choices = AuthoringInterview
            .For(null, carried)
            .SelectMany(section => section.Questions)
            .Single(asked => asked.Id == "identity.pattern")
            .Choices;

        Assert.Equal(["ask-the-corpus", "red-flag-review"], served.Select(pattern => pattern.Id));
        Assert.Equal(served.Select(pattern => pattern.Id), choices);
    }

    /// <summary>Every question names the slot its answer lands in, and an enum offers something.</summary>
    [Fact]
    public void EveryQuestionCarriesASlotAndItsChoices()
    {
        foreach (var question in AuthoringInterview.For(pattern: null).SelectMany(section => section.Questions))
        {
            Assert.False(string.IsNullOrWhiteSpace(question.MapsTo), question.Id);

            if (question.Kind == InterviewKinds.Enum)
            {
                Assert.NotEmpty(question.Choices);
            }
        }
    }
}
