namespace Munarium.Retrieval;

using System.Text.Json;
using System.Text.Json.Serialization;
using Munarium.Providers;
using Munarium.Runbooks;

/// <summary>What a runbook's query-expansion step produced, and what it cost.</summary>
/// <param name="Terms">The accepted variants, in the order the model offered them.</param>
/// <param name="Provider">The provider dialect that answered.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="InputTokens">What the call cost to send.</param>
/// <param name="OutputTokens">What the call cost to generate.</param>
public sealed record QueryExpansionApplied(
    IReadOnlyList<string> Terms,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens);

/// <summary>Why a query-expansion step produced nothing, when the runbook let that pass.</summary>
/// <param name="Reason">What went wrong, in the words the failure carried.</param>
public sealed record QueryExpansionUnavailable(string Reason);

/// <summary>The result of a query-expansion step: what it added, or why it added nothing.</summary>
public readonly union QueryExpansionResult(QueryExpansionApplied, QueryExpansionUnavailable);

/// <summary>
/// Widens a query with the lexical variants a model proposes, through the task level a runbook pins for it.
/// </summary>
/// <remarks>
/// A paid step like the intent classification, and modelled the same way: the task is optional, the prompt constrains
/// what may come back, and the answer is parsed against a closed rule rather than trusted. The rule is strict on
/// purpose - lowercase single words, two to forty characters, letters plus hyphen and apostrophe, and nothing the
/// question already says - because the expansion widens <em>wording</em>: a term the model capitalised is refused rather
/// than folded down, since a term that arrived as a name, a date or a phrase is a fact the model invented about the
/// answer, and candidate selection must not carry those.
/// </remarks>
public static class QueryExpansion
{
    /// <summary>The task level this resolves through, which is the original's own name for it.</summary>
    public const string Task = TaskLevels.QueryExpansion;

    /// <summary>The most terms an expansion may add when a runbook names no ceiling.</summary>
    public const int DefaultMaxTerms = 12;

    /// <summary>
    /// The completion ceiling an expansion gets when a runbook names none.
    /// </summary>
    /// <remarks>
    /// The original reads the deployment's configured <c>query_expansion</c> budget here. This port has no such setting
    /// yet, so the ceiling is a constant and it is the port's rather than the original's - which is written down because
    /// a budget that quietly differs is a cost that quietly differs.
    /// </remarks>
    public const int DefaultMaxTokens = 128;

    /// <summary>Reports whether a runbook pins the query-expansion task.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Whether the task is declared.</returns>
    public static bool IsPinned(RunbookDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.Spec.Models is { } models && models.Tasks.ContainsKey(Task);
    }

    /// <summary>Builds the prompt, in the original's words.</summary>
    /// <remarks>
    /// Every constraint in it is load-bearing: a model asked to "expand" a query answers it, or offers names, dates and
    /// phrases that read like facts, so the prompt asks for what the parser will accept rather than relying on the
    /// refusal to catch the difference.
    /// </remarks>
    /// <param name="query">The question to widen.</param>
    /// <param name="maxTerms">How many variants may be accepted.</param>
    /// <returns>The prompt.</returns>
    public static string Prompt(string query, int maxTerms)
    {
        ArgumentNullException.ThrowIfNull(query);

        return $"Generate up to {maxTerms} generic lexical variants that may occur in documents relevant to the search "
            + "question below. Return ONLY a JSON array of lowercase, single-word strings. Supply synonyms, related "
            + "action nouns/verbs, and older/common wording. Do not answer the question. Do not add names, places, "
            + "organizations, dates, numbers, or facts that are not already in the question. Omit words already "
            + $"present.\n\nSearch question: {query}";
    }

    /// <summary>Reads the accepted terms out of a model's answer.</summary>
    /// <remarks>
    /// Strict first, with the same rescue the original uses: an answer that wraps its array in prose is read anyway by
    /// taking the first <c>[</c> to the last <c>]</c>, because that is a formatting failure rather than a wrong answer.
    /// <para>
    /// An answer with no array at all is <em>no terms</em> rather than a failure, which is what the original does: its
    /// rescue ends in an empty array, so its parse error is unreachable and the only failure a runbook can mark
    /// <c>required</c> is the provider's. Being stricter here would fail turns the original answers, which is a
    /// divergence a port should not introduce while calling itself faithful.
    /// </para>
    /// <para>
    /// A term has to be lowercase <em>as it arrived</em> - the original's rule, and not a slip: folding a capitalised term
    /// down would let a name into candidate selection, which is the one thing the rule exists to stop. Hyphens and
    /// apostrophes are allowed because words carry them; anything else is refused.
    /// </para>
    /// </remarks>
    /// <param name="answer">What the model said.</param>
    /// <param name="query">The question, whose own words are not variants of itself.</param>
    /// <param name="maxTerms">How many terms may be accepted.</param>
    /// <returns>The accepted terms, in the order they were offered.</returns>
    public static IReadOnlyList<string> Parse(string answer, string query, int maxTerms)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(query);

        var seen = new HashSet<string>(CollectionSelection.WordTokens(query), StringComparer.Ordinal);
        var terms = new List<string>();

        foreach (var raw in ArrayOf(answer) ?? [])
        {
            var trimmed = raw.Trim();
            var term = trimmed.ToLowerInvariant();

            var valid = term.Length is >= 2 and <= 40
                && string.Equals(trimmed, term, StringComparison.Ordinal)
                && term.Any(char.IsLetter)
                && term.All(character => char.IsLetter(character) || character is '-' or '\'');

            if (!valid || !seen.Add(term))
            {
                continue;
            }

            terms.Add(term);

            if (terms.Count == maxTerms)
            {
                break;
            }
        }

        return terms;
    }

    /// <summary>Widens a query's text with the accepted variants.</summary>
    /// <remarks>
    /// The original carries the variants beside the query and weights them against it; this retriever takes one text per
    /// search, so the variants are appended to the text the lexical leg reads while the embedding is taken from the query
    /// alone - the wording widens, the meaning does not. That is the difference between the two, written where it lives:
    /// a weighted blend of the original and the expanded formulation is not something this engine can express.
    /// </remarks>
    /// <param name="query">The question as asked.</param>
    /// <param name="terms">The accepted variants.</param>
    /// <returns>The text to search with.</returns>
    public static string Widen(string query, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(terms);

        return terms.Count == 0 ? query : query + " " + string.Join(' ', terms);
    }

    /// <summary>
    /// Widens a query when the runbook declares the step, asking the model only then.
    /// </summary>
    /// <remarks>
    /// A failure is a result rather than an exception, for the same reason a provider's refusal is a block: "the step did
    /// not happen" is something a turn has to be able to carry, and the runbook decides whether it is fatal. The catch is
    /// deliberately not narrowed to a set of types: this port does not own the provider a deployment brings, so it has no
    /// closed set to name - and the original treats every failure the same way.
    /// </remarks>
    /// <param name="document">The pinned runbook.</param>
    /// <param name="query">The question as asked.</param>
    /// <param name="model">The model to ask, when the step runs.</param>
    /// <param name="modelId">The model to ask for, as the provider names it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What it added, or why it added nothing; <see langword="null"/> when the runbook declares no step.</returns>
    public static async ValueTask<QueryExpansionResult?> ResolveAsync(
        RunbookDocument document,
        string query,
        IModelProvider model,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(model);

        if (document.Spec.Retrieval?.ModelQueryExpansion is not { } spec || !IsPinned(document))
        {
            return null;
        }

        try
        {
            var response = await model
                .CompleteAsync(
                    new CompletionRequest
                    {
                        Model = modelId,
                        Prompt = Prompt(query, spec.MaxTerms),
                        MaxTokens = spec.MaxTokens ?? DefaultMaxTokens,

                        // Widening is not composing: the same question has to widen the same way on two turns, or the
                        // candidates a turn considered stop being a function of the question.
                        Temperature = 0.0,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return new QueryExpansionApplied(
                Parse(response.Text, query, spec.MaxTerms),
                model.Id.Value,
                response.Model,
                response.Usage.InputTokens,
                response.Usage.OutputTokens);
        }
        catch (Exception failure)
        {
            return new QueryExpansionUnavailable(failure.Message);
        }
    }

    /// <summary>Reads a JSON array of strings out of an answer, with the fenced rescue.</summary>
    /// <param name="answer">The answer.</param>
    /// <returns>The strings, or <see langword="null"/> when there was no array to read.</returns>
    private static List<string>? ArrayOf(string answer)
    {
        if (Strings(answer) is { } whole)
        {
            return whole;
        }

        var start = answer.IndexOf('[', StringComparison.Ordinal);
        var end = answer.LastIndexOf(']');

        return start >= 0 && end > start ? Strings(answer[start..(end + 1)]) : null;
    }

    /// <summary>Parses one span as a JSON array of strings.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The strings, or <see langword="null"/> when the span is not one.</returns>
    private static List<string>? Strings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ExpansionJson.Default.ListString);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The JSON this step parses, generated rather than reflected.
/// </summary>
/// <remarks>
/// The runtime is AOT-analysed here, so a reflective deserializer would be a build warning now and a failure under
/// NativeAOT later - which is what the analyzer said when this was written with the generic overload.
/// </remarks>
[JsonSerializable(typeof(List<string>))]
internal sealed partial class ExpansionJson : JsonSerializerContext;
