namespace Munarium.Core.Tests.Retrieval;

using Munarium.Retrieval;

/// <summary>Tests for the query-expansion step: its prompt, its parser, and what widening a query means here.</summary>
public class QueryExpansionTests
{
    private const string Question = "how many contracts lapse?";

    /// <summary>
    /// The parser keeps what the prompt's contract allows and refuses everything else.
    /// </summary>
    /// <remarks>
    /// The refusals are the point. A capitalised term is refused rather than folded down, because folding it down is what
    /// would let a name into candidate selection; a single character, a phrase and a token with punctuation are refused;
    /// and a word the question already carries is not a variant of it - which is why <c>lapse</c> and <c>many</c> are in
    /// the answer above and in the expectation below, where they are refused for two different reasons.
    /// </remarks>
    [Fact]
    public void TheParserKeepsSingleLowercaseWordsAndRefusesTheRest()
    {
        var terms = QueryExpansion.Parse(
            """
            ["elapse", "expire", "Renew", "a", "re-new", "don't", "many", "lapse", "elapse", "two words", "3", "lapse!"]
            """,
            Question,
            maxTerms: 12);

        Assert.Equal(["elapse", "expire", "re-new", "don't"], terms);
    }

    /// <summary>The cap is the runbook's, and the order the model offered survives it.</summary>
    [Fact]
    public void TheRunbooksCapIsTheCap() =>
        Assert.Equal(
            ["expire", "renew"],
            QueryExpansion.Parse("[\"expire\", \"renew\", \"elapse\"]", Question, maxTerms: 2));

    /// <summary>
    /// An answer without an array is no terms rather than a failure, which is what the original does.
    /// </summary>
    /// <remarks>
    /// Its rescue ends in an empty array, so its parse error is unreachable - and being stricter here would fail turns
    /// the original answers. A fenced array is read anyway, because that is a formatting failure and not a wrong answer.
    /// </remarks>
    [Fact]
    public void AnAnswerWithoutAnArrayIsNoTerms()
    {
        Assert.Empty(QueryExpansion.Parse("I cannot help with that.", Question, maxTerms: 5));

        Assert.Equal(
            ["expire", "renew"],
            QueryExpansion.Parse("Here you go:\n[\"expire\", \"renew\"]\nHope that helps.", Question, maxTerms: 5));
    }

    /// <summary>Widening appends to the question, and an empty expansion leaves it exactly as it was.</summary>
    [Fact]
    public void WideningAppendsAndAnEmptyExpansionChangesNothing()
    {
        Assert.Equal("how many contracts lapse? expire", QueryExpansion.Widen(Question, ["expire"]));
        Assert.Equal(Question, QueryExpansion.Widen(Question, []));
    }

    /// <summary>The prompt carries the cap and the question, and asks for what the parser will accept.</summary>
    [Fact]
    public void ThePromptCarriesTheCapAndTheQuestion()
    {
        var prompt = QueryExpansion.Prompt(Question, maxTerms: 7);

        Assert.Contains("up to 7", prompt, StringComparison.Ordinal);
        Assert.Contains(Question, prompt, StringComparison.Ordinal);
        Assert.Contains("Return ONLY a JSON array of lowercase, single-word strings", prompt, StringComparison.Ordinal);
    }
}
