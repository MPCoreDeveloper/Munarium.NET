namespace Munarium.Sessions;

using Munarium.Evidence;
using Munarium.Providers;
using Munarium.Runbooks;

/// <summary>
/// What a question is asking for, resolved through the model a runbook pins for the intent task.
/// </summary>
/// <remarks>
/// The task is optional. A runbook that pins no <c>intent</c> task gets an intent that says so: the question, no kind,
/// and <see cref="QueryIntent.Explicit"/> true - which is what tells everything downstream that nothing was modelled and
/// nothing semantic should be attempted. That is the same distinction the evidence hierarchy carries, and it is why a
/// missing task is not an error: the document path is a complete answer, not a degraded one.
/// <para>
/// When the task is pinned the answer is parsed against a <em>closed</em> vocabulary, and an answer that names none of
/// them becomes no kind at all rather than being passed through. An intent kind nothing downstream understands is worse
/// than no intent kind, because it looks like information.
/// </para>
/// </remarks>
public static class IntentResolution
{
    /// <summary>The task level this resolves through.</summary>
    public const string Task = TaskLevels.Intent;

    /// <summary>
    /// The completion ceiling a classification gets.
    /// </summary>
    /// <remarks>
    /// One word, so the ceiling is tiny and it is a ceiling rather than spend: the original's built-in is thirty-two
    /// tokens, and a classifier that needs more than that is answering something other than the question asked.
    /// </remarks>
    public const int DefaultMaxTokens = 32;

    /// <summary>Gets the vocabulary an intent kind may come from.</summary>
    public static IReadOnlyList<string> Kinds { get; } =
        ["lookup", "aggregation", "comparison", "enumeration", "timeline", "other"];

    /// <summary>
    /// Answers whether a runbook pins the intent task.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>Whether the task is declared.</returns>
    public static bool IsPinned(RunbookDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.Spec.Models is { } models && models.Tasks.ContainsKey(Task);
    }

    /// <summary>
    /// Builds the classification prompt.
    /// </summary>
    /// <remarks>
    /// The vocabulary is spelled out and the question is labelled, in the original's words: a classifier asked to
    /// classify is otherwise a model asked to answer, and a model that answers has told us nothing about the kind.
    /// </remarks>
    /// <param name="question">The question.</param>
    /// <returns>The prompt.</returns>
    public static string Prompt(string question)
    {
        ArgumentNullException.ThrowIfNull(question);

        return "Classify what this question is asking for. Answer with ONE lowercase word from exactly this list: "
            + "lookup, aggregation, comparison, enumeration, timeline, other. Do not answer the question itself."
            + $"\n\nQuestion: {question}";
    }

    /// <summary>
    /// Parses a classification answer.
    /// </summary>
    /// <param name="answer">What the model said.</param>
    /// <returns>The kind, or <see langword="null"/> when the answer names none of them.</returns>
    public static string? ParseKind(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var text = answer.Trim().ToLowerInvariant();

        return Kinds.FirstOrDefault(kind => text.Contains(kind, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds the intent of a turn that was not modelled, because the runbook pins no intent task.
    /// </summary>
    /// <param name="question">The question.</param>
    /// <returns>The explicit intent.</returns>
    public static QueryIntent Unmodelled(string question)
    {
        ArgumentNullException.ThrowIfNull(question);

        return new QueryIntent { Question = question, Explicit = true };
    }

    /// <summary>
    /// Builds the intent from an answer a model gave.
    /// </summary>
    /// <param name="question">The question.</param>
    /// <param name="answer">The model's answer.</param>
    /// <returns>The intent, which is modelled rather than explicit whether or not a kind was recognised.</returns>
    public static QueryIntent Classify(string question, string answer) => new()
    {
        Question = question,
        Kind = ParseKind(answer),
        Explicit = false,
    };

    /// <summary>
    /// Resolves what a question is asking, asking the model only when the runbook pinned the task.
    /// </summary>
    /// <remarks>
    /// The model id arrives resolved rather than as a tier: turning a runbook's tier into a concrete model is a
    /// deployment's business, because that is where the credentials and the provider inventory live.
    /// </remarks>
    /// <param name="document">The pinned runbook.</param>
    /// <param name="question">The question as asked.</param>
    /// <param name="model">The model to ask, when one is needed.</param>
    /// <param name="modelId">The model to ask for, as the provider names it.</param>
    /// <param name="maxTokens">The classification's ceiling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The intent.</returns>
    public static async ValueTask<QueryIntent> ResolveAsync(
        RunbookDocument document,
        string question,
        IModelProvider model,
        string modelId,
        int maxTokens = DefaultMaxTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(model);

        if (!IsPinned(document))
        {
            return Unmodelled(question);
        }

        var response = await model
            .CompleteAsync(
                new CompletionRequest
                {
                    Model = modelId,
                    Prompt = Prompt(question),
                    MaxTokens = maxTokens,

                    // Classifying is not composing: any temperature here would make the same question classify
                    // differently on two turns, and the plan a turn runs under has to be a function of the question.
                    Temperature = 0.0,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Classify(question, response.Text);
    }
}
