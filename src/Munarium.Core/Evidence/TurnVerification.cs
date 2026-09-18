namespace Munarium.Evidence;

/// <summary>
/// The deterministic checks a turn runs over its own answer.
/// </summary>
/// <remarks>
/// Both are pure string work over data the turn already holds: no model judges anything, and a violation is a fact
/// rather than an opinion. They exist because an answer can be fluent, confident and about documents nobody served, and
/// the only way to know is to check it against what the model was actually given.
/// <para>
/// The correction is bounded and paid for: a violating answer gets a small number of corrective completions with the
/// violations attached, and then stands as it is with the outcome recorded.
/// </para>
/// </remarks>
public static class TurnVerification
{
    /// <summary>
    /// Quoted spans shorter than this are ignored.
    /// </summary>
    /// <remarks>
    /// Short quotes - a bare "yes", a name - collide with ordinary prose and are not grounding claims. Only substantial
    /// spans have to resolve.
    /// </remarks>
    public const int MinQuoteChars = 15;

    /// <summary>
    /// Checks the quoted spans of an answer against the text that was served.
    /// </summary>
    /// <param name="answer">The answer.</param>
    /// <param name="servedTexts">Every text the turn served.</param>
    /// <returns>The spans that do not resolve.</returns>
    public static IReadOnlyList<string> CheckQuotes(string answer, IReadOnlyList<string> servedTexts)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(servedTexts);

        var served = servedTexts.Select(NormalizeWhitespace).ToList();

        return
        [
            .. QuotedSpans(answer).Where(span =>
                !served.Any(text => text.Contains(NormalizeWhitespace(span), StringComparison.Ordinal))),
        ];
    }

    /// <summary>
    /// Checks the bracketed labels of an answer against the labels that were served.
    /// </summary>
    /// <remarks>
    /// A label has to contain a slash, and a bracketed token without one - <c>[sic]</c>, <c>[1]</c> - is not a citation
    /// and passes: the check never guesses. The served list carries both the chunk labels a context block prints and the
    /// source paths a hit came from, because an answer may cite either.
    /// </remarks>
    /// <param name="answer">The answer.</param>
    /// <param name="servedLabels">Every label the turn served.</param>
    /// <returns>The labels that were not served, deduplicated.</returns>
    public static IReadOnlyList<string> CheckCitations(string answer, IReadOnlyList<string> servedLabels)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(servedLabels);

        var violations = new List<string>();
        var rest = answer.AsSpan();

        while (true)
        {
            var open = rest.IndexOf('[');

            if (open < 0)
            {
                break;
            }

            var after = rest[(open + 1)..];
            var close = after.IndexOf(']');

            if (close < 0)
            {
                break;
            }

            var token = after[..close].Trim().ToString();

            if (token.Contains('/', StringComparison.Ordinal)
                && token.Length > 0
                && !token.Contains('\n', StringComparison.Ordinal)
                && !servedLabels.Contains(token, StringComparer.Ordinal))
            {
                violations.Add(token);
            }

            rest = after[(close + 1)..];
        }

        // Sorted first, then deduplicated: the corrective prompt should name each violation once, whatever order the
        // model cited them in, and sorting is what makes "once" complete rather than merely adjacent.
        violations.Sort(StringComparer.Ordinal);

        return [.. violations.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Builds the corrective instruction for one retry.
    /// </summary>
    /// <remarks>
    /// Violations first, because the model has to know exactly what failed; then the rules it broke; then the original
    /// task with its full served context re-attached. The retry is deliberately a re-ask rather than a targeted fetch:
    /// the second attempt has everything the first had, which is what makes "quote only what is here" an instruction it
    /// can actually follow.
    /// </remarks>
    /// <param name="originalPrompt">The prompt the turn sent.</param>
    /// <param name="previousAnswer">The answer that failed.</param>
    /// <param name="quoteViolations">The spans that did not resolve.</param>
    /// <param name="citationViolations">The labels that were not served.</param>
    /// <returns>The corrective prompt.</returns>
    public static string CorrectivePrompt(
        string originalPrompt,
        string previousAnswer,
        IReadOnlyList<string> quoteViolations,
        IReadOnlyList<string> citationViolations)
    {
        ArgumentNullException.ThrowIfNull(originalPrompt);
        ArgumentNullException.ThrowIfNull(previousAnswer);
        ArgumentNullException.ThrowIfNull(quoteViolations);
        ArgumentNullException.ThrowIfNull(citationViolations);

        var parts = new List<string>
        {
            "Your previous answer failed deterministic verification and must be revised.\n",
        };

        if (quoteViolations.Count > 0)
        {
            parts.Add(
                "\nThese quoted passages do NOT appear in the provided context - quote only text that appears "
                    + "verbatim, or remove the quotation marks and paraphrase:\n");

            parts.AddRange(quoteViolations.Select(quotation => $"  - \"{quotation}\"\n"));
        }

        if (citationViolations.Count > 0)
        {
            parts.Add(
                "\nThese citations name content that was NOT provided - cite only the bracketed labels present in the "
                    + "context, or state that the information is not in the provided documents:\n");

            parts.AddRange(citationViolations.Select(citation => $"  - [{citation}]\n"));
        }

        parts.Add("\nYour previous answer:\n");
        parts.Add(previousAnswer);
        parts.Add("\n\n--- Original task, with the provided context ---\n");
        parts.Add(originalPrompt);

        return string.Concat(parts);
    }

    /// <summary>Collapses every run of whitespace to one space, and trims the result.</summary>
    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Extracts the substantial double-quoted spans, straight or curly.
    /// </summary>
    /// <remarks>
    /// An unterminated span is dropped rather than reported: an answer that opened a quotation mark and never closed it
    /// has a typography problem, and reporting everything after it as an ungrounded quote would bury the real finding.
    /// </remarks>
    private static List<string> QuotedSpans(string text)
    {
        var spans = new List<string>();
        var current = new System.Text.StringBuilder();
        var inside = false;

        foreach (var character in text)
        {
            if (character is '"' or '\u201c' or '\u201d')
            {
                if (inside)
                {
                    if (current.ToString().Trim().Length >= MinQuoteChars)
                    {
                        spans.Add(current.ToString());
                    }

                    current.Clear();
                    inside = false;
                }
                else
                {
                    inside = true;
                }

                continue;
            }

            if (inside)
            {
                current.Append(character);
            }
        }

        return spans;
    }
}
