namespace Munarium.Core.Tests.Evidence;

using Munarium.Evidence;

/// <summary>
/// Tests for the deterministic checks a turn runs over its own answer - the same three the original carries, because
/// what a violation is has to mean the same thing on both sides of the port.
/// </summary>
public class TurnVerificationTests
{
    /// <summary>
    /// Quotes resolve across whitespace differences, a fabricated substantial quote is a violation, and short quotes
    /// never gate.
    /// </summary>
    [Fact]
    public void QuotesResolveWhitespaceNormalizedAndShortSpansPass()
    {
        string[] served = ["The  harbor bell\nrang twice at dawn, said the keeper."];

        Assert.Empty(TurnVerification.CheckQuotes(
            "The log notes \"The harbor bell rang twice at dawn\" clearly.",
            served));

        var violations = TurnVerification.CheckQuotes(
            "It says \"the lighthouse was painted crimson that spring\" too.",
            served);

        Assert.Equal(["the lighthouse was painted crimson that spring"], violations);
        Assert.Empty(TurnVerification.CheckQuotes("He said \"yes\" firmly.", served));
    }

    /// <summary>
    /// Citations have to name served labels, and a bracket that is not a citation - <c>[sic]</c>, <c>[1]</c> - is not
    /// the check's business.
    /// </summary>
    [Fact]
    public void CitationsMustNameServedLabelsAndPlainBracketsPass()
    {
        string[] served = ["docs/chunk-01", "kb/faq-9", "cl/one.txt"];

        Assert.Empty(TurnVerification.CheckCitations("See [docs/chunk-01] and [kb/faq-9].", served));
        Assert.Equal(
            ["docs/chunk-99"],
            TurnVerification.CheckCitations("Per [docs/chunk-99] the cap is 60%.", served));
        Assert.Empty(TurnVerification.CheckCitations("The report [sic] cites [1] and [2].", served));
    }

    /// <summary>
    /// A violation is named once, however often the model repeated it, because the corrective prompt is a list of
    /// findings rather than a tally of mistakes.
    /// </summary>
    [Fact]
    public void RepeatedCitationsAreNamedOnce()
    {
        string[] served = ["docs/chunk-01"];

        Assert.Equal(
            ["a/one", "docs/chunk-99"],
            TurnVerification.CheckCitations(
                "Per [docs/chunk-99] and [a/one], and again [docs/chunk-99].",
                served));
    }

    /// <summary>
    /// A corrective prompt carries the violations, the answer they were found in, and the original task with its served
    /// context - the retry is a re-ask, not a targeted fetch.
    /// </summary>
    [Fact]
    public void CorrectivePromptCarriesViolationsAnswerAndContext()
    {
        var prompt = TurnVerification.CorrectivePrompt(
            "Context: [a/b] text\n\nQ: what?",
            "Previous answer.",
            ["ghost quote"],
            ["a/zz"]);

        Assert.Contains("ghost quote", prompt, StringComparison.Ordinal);
        Assert.Contains("[a/zz]", prompt, StringComparison.Ordinal);
        Assert.Contains("Previous answer.", prompt, StringComparison.Ordinal);
        Assert.Contains("Q: what?", prompt, StringComparison.Ordinal);
    }
}
