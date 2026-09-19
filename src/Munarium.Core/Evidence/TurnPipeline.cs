namespace Munarium.Evidence;

using Munarium.Providers;
using Munarium.Retrieval;

/// <summary>
/// What a turn is asked to do.
/// </summary>
/// <param name="Question">The question as asked.</param>
/// <param name="PromptTemplate">The template, which references <c>{context}</c> and <c>{query}</c>.</param>
/// <param name="MaxTokens">The completion ceiling for the first attempt.</param>
/// <param name="Verification">Which deterministic checks run over the answer.</param>
public sealed record TurnRequest(
    string Question,
    string PromptTemplate,
    int MaxTokens,
    TurnVerificationChecks Verification);

/// <summary>
/// Which checks run, and what a failed answer may cost.
/// </summary>
/// <param name="Quotes">Whether quoted spans have to resolve in the served text.</param>
/// <param name="Citations">Whether bracketed labels have to name served content.</param>
/// <param name="MaxRetries">
/// How many corrective completions an answer that fails may cost. Every one of them is a paid call, so the count is
/// small and bounded rather than generous.
/// </param>
public sealed record TurnVerificationChecks(bool Quotes, bool Citations, int MaxRetries = 1)
{
    /// <summary>Gets a value indicating whether any check runs at all.</summary>
    public bool Enabled => Quotes || Citations;

    /// <summary>Gets the names of the checks that ran, in the order a report lists them.</summary>
    public IReadOnlyList<string> Names =>
    [
        .. new[] { (Quotes, "quotes"), (Citations, "citations") }
            .Where(check => check.Item1)
            .Select(check => check.Item2),
    ];
}

/// <summary>
/// What a turn produced, and what it cost.
/// </summary>
public sealed record TurnOutcome
{
    /// <summary>Gets what the hierarchy decided, or <see langword="null"/> when the turn ran outside any profile.</summary>
    public EvidenceHierarchyDecision? Decision { get; init; }

    /// <summary>Gets the composed context the model was given.</summary>
    public required string Context { get; init; }

    /// <summary>Gets the layers whose blocks did not fit the budget.</summary>
    public required IReadOnlyList<string> LayersDropped { get; init; }

    /// <summary>Gets the blocks the turn was composed from.</summary>
    public required IReadOnlyList<LayerBlock> Blocks { get; init; }

    /// <summary>Gets the answer, which is the last one the model produced.</summary>
    public required string Answer { get; init; }

    /// <summary>Gets the checks that ran.</summary>
    public required IReadOnlyList<string> Checks { get; init; }

    /// <summary>Gets the violations the first pass found, before any correction.</summary>
    public required IReadOnlyList<string> FirstPassViolations { get; init; }

    /// <summary>Gets the violations the last pass found, or an empty list when the answer stands clean.</summary>
    public required IReadOnlyList<string> Violations { get; init; }

    /// <summary>Gets how many corrective completions were paid for.</summary>
    public required int Retries { get; init; }

    /// <summary>Gets how many completions the turn paid for.</summary>
    public required int Completions { get; init; }

    /// <summary>Gets how many tokens the turn's completions consumed.</summary>
    public required int InputTokens { get; init; }

    /// <summary>Gets how many tokens the turn's completions produced.</summary>
    public required int OutputTokens { get; init; }

    /// <summary>Gets a value indicating whether any attempt was retried for stopping early.</summary>
    public required bool RetriedForTruncation { get; init; }

    /// <summary>Gets a value indicating whether the answer stands with violations recorded rather than fixed.</summary>
    public bool AnswerStandsWithViolations => Violations.Count > 0;
}

/// <summary>
/// The result of a turn: what it produced, or the required layer that could not answer.
/// </summary>
/// <remarks>
/// A union rather than an exception, for the same reason the runner's is one: a required layer that could not answer is
/// an outcome a caller has to disclose, not a bug - and the union makes it a case that has to be handled.
/// </remarks>
public readonly union TurnResult(TurnOutcome, RequiredLayerUnavailable);

/// <summary>
/// Runs one turn: the hierarchy, the context, the completion, and the checks over the answer.
/// </summary>
/// <remarks>
/// This is the kernel's half of a turn, and it resolves nothing: the profile, the plan, the model, the template and the
/// budget all arrive already chosen, because choosing them is a deployment's business - which model, which keys, which
/// tenant. What this owns is the order: evidence first, then an answer, then the checks that decide whether the answer
/// is allowed to stand as it is.
/// <para>
/// A required layer that cannot answer stops the turn <em>before</em> a model is paid for. That is the one place a
/// refusal is fatal rather than disclosed, and it is what <c>required</c> means.
/// </para>
/// </remarks>
public static class TurnPipeline
{
    /// <summary>The context budget a caller that names none gets.</summary>
    public const int DefaultContextBudget = 16_000;

    /// <summary>
    /// The most corrective completions one turn may pay for, however a runbook phrases the request.
    /// </summary>
    /// <remarks>
    /// The original clamps to two. A document cannot raise it, because a document is not a budget: every retry is a
    /// paid call to a model someone else is billed for.
    /// </remarks>
    public const int MaxCorrectiveRetries = 2;

    private const string QuotePrefix = "quote: ";

    private const string CitationPrefix = "citation: ";

    /// <summary>
    /// Runs a turn.
    /// </summary>
    /// <param name="plan">The plan to execute.</param>
    /// <param name="request">What the turn is asked to do.</param>
    /// <param name="providers">The evidence providers, in trust order.</param>
    /// <param name="documentLayer">Runs the document path for a layer.</param>
    /// <param name="servedLabels">Reads the citable labels out of a document retrieval.</param>
    /// <param name="model">The model to answer with.</param>
    /// <param name="modelId">The model to ask for, as the provider names it.</param>
    /// <param name="contextBudget">How many characters of composed context the prompt may carry.</param>
    /// <param name="onProgress">An optional listener; the turn's result never depends on one being present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the turn produced, or the required layer that could not answer.</returns>
    public static async ValueTask<TurnResult> ExecuteAsync(
        EvidencePlan plan,
        TurnRequest request,
        IReadOnlyList<IEvidenceProvider> providers,
        Func<EvidenceLayer, CancellationToken, ValueTask<RetrievalResult>> documentLayer,
        Func<RetrievalResult, IReadOnlyList<string>> servedLabels,
        IModelProvider model,
        string modelId,
        int contextBudget = DefaultContextBudget,
        Action<TurnProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(documentLayer);
        ArgumentNullException.ThrowIfNull(servedLabels);
        ArgumentNullException.ThrowIfNull(model);

        var executed = await HierarchyRunner
            .ExecuteAsync(plan, providers, documentLayer, hierarchy => onProgress?.Invoke(hierarchy), cancellationToken)
            .ConfigureAwait(false);

        return executed switch
        {
            HierarchyOutcome collected => await ComposeThenAnswerAsync(
                plan,
                request,
                collected,
                servedLabels,
                model,
                modelId,
                contextBudget,
                onProgress,
                cancellationToken).ConfigureAwait(false),
            RequiredLayerUnavailable unavailable => unavailable,
        };
    }

    /// <summary>
    /// Composes the hierarchy's context into a prompt and answers over it.
    /// </summary>
    private static async ValueTask<TurnOutcome> ComposeThenAnswerAsync(
        EvidencePlan plan,
        TurnRequest request,
        HierarchyOutcome collected,
        Func<RetrievalResult, IReadOnlyList<string>> servedLabels,
        IModelProvider model,
        string modelId,
        int contextBudget,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var composed = HierarchyComposer.Compose(plan, collected.Blocks, contextBudget);
        var documents = collected.Documents;
        var labels = documents is null ? [] : servedLabels(documents);
        var texts = documents is null ? [] : documents.Chunks.Select(chunk => chunk.Text).ToList();

        onProgress?.Invoke(new TurnComposed(
            composed.LayersUsed,
            composed.Context.Length,
            composed.LayersDropped));

        return await AnswerAsync(
            request,
            Render(request, composed.Context),
            composed.Context,
            composed.LayersDropped,
            collected.Blocks,
            collected.Decision,
            texts,
            labels,
            model,
            modelId,
            onProgress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers over text that was already gathered, and made citable by the caller.
    /// </summary>
    /// <remarks>
    /// The document path's half of the pipeline: no hierarchy ran, so the outcome carries no decision rather than a
    /// synthetic one. An empty decision would claim a hierarchy ran and decided nothing, which is the one thing the
    /// audit has to be able to tell apart from a turn that ran outside any profile.
    /// </remarks>
    /// <param name="request">What the turn is asked to do.</param>
    /// <param name="context">The text the model is given, already rendered.</param>
    /// <param name="servedTexts">Every text the answer may quote.</param>
    /// <param name="servedLabels">Every label the answer may cite.</param>
    /// <param name="model">The model to answer with.</param>
    /// <param name="modelId">The model to ask for, as the provider names it.</param>
    /// <param name="onProgress">An optional listener; the turn's result never depends on one being present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer, what it cost, and what was checked.</returns>
    public static async ValueTask<TurnOutcome> AnswerOverTextAsync(
        TurnRequest request,
        string context,
        IReadOnlyList<string> servedTexts,
        IReadOnlyList<string> servedLabels,
        IModelProvider model,
        string modelId,
        Action<TurnProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(servedTexts);
        ArgumentNullException.ThrowIfNull(servedLabels);
        ArgumentNullException.ThrowIfNull(model);

        return await AnswerAsync(
            request,
            Render(request, context),
            context,
            [],
            [],
            null,
            servedTexts,
            servedLabels,
            model,
            modelId,
            onProgress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the model, reads the answer back against what was served, and repairs it a bounded number of times.
    /// </summary>
    /// <remarks>
    /// Two retries live here and they are different things. The truncation retry fires when the model stopped early -
    /// a reasoning model spends hidden tokens from the same budget - and it re-asks once at four times the ceiling,
    /// because a ceiling is not spend. The corrective retries fire on a failed check and re-ask with the violations
    /// attached, riding whatever ceiling is current so a repaired answer gets the same headroom.
    /// </remarks>
    private static async ValueTask<TurnOutcome> AnswerAsync(
        TurnRequest request,
        string prompt,
        string context,
        IReadOnlyList<string> layersDropped,
        IReadOnlyList<LayerBlock> blocks,
        EvidenceHierarchyDecision? decision,
        IReadOnlyList<string> servedTexts,
        IReadOnlyList<string> labels,
        IModelProvider model,
        string modelId,
        Action<TurnProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var budget = request.MaxTokens;
        var answer = await CompleteAsync(model, modelId, prompt, budget, cancellationToken).ConfigureAwait(false);
        var completions = 1;
        var inputTokens = answer.Usage.InputTokens;
        var outputTokens = answer.Usage.OutputTokens;
        var retriedForTruncation = answer.IsTruncated;

        onProgress?.Invoke(Completed(0, model, answer));

        if (retriedForTruncation)
        {
            budget = request.MaxTokens * 4;
            answer = await CompleteAsync(model, modelId, prompt, budget, cancellationToken).ConfigureAwait(false);
            completions++;
            inputTokens += answer.Usage.InputTokens;
            outputTokens += answer.Usage.OutputTokens;

            // The re-ask is attempt zero as well, because it is the same attempt: the model stopped early rather than
            // answer badly, and numbering it apart would make a stream look like a turn that retried a wrong answer.
            onProgress?.Invoke(Completed(0, model, answer));
        }

        var checks = request.Verification;
        var violations = Check(answer.Text, checks, servedTexts, labels);
        var firstPass = violations;

        onProgress?.Invoke(new TurnVerified(0, checks.Names, violations.Count));

        var retries = 0;

        // Clamped exactly as the original clamps it: every retry is a paid call, so the ceiling on them is small and
        // cannot be raised by a document.
        while (violations.Count > 0 && retries < Math.Clamp(checks.MaxRetries, 0, MaxCorrectiveRetries))
        {
            retries++;

            var corrective = TurnVerification.CorrectivePrompt(
                prompt,
                answer.Text,
                QuotesOf(violations),
                CitationsOf(violations));

            answer = await CompleteAsync(model, modelId, corrective, budget, cancellationToken).ConfigureAwait(false);
            completions++;
            inputTokens += answer.Usage.InputTokens;
            outputTokens += answer.Usage.OutputTokens;

            onProgress?.Invoke(Completed(retries, model, answer));

            violations = Check(answer.Text, checks, servedTexts, labels);

            onProgress?.Invoke(new TurnVerified(retries, checks.Names, violations.Count));
        }

        return new TurnOutcome
        {
            Decision = decision,
            Context = context,
            LayersDropped = layersDropped,
            Blocks = blocks,
            Answer = answer.Text,
            Checks = checks.Names,
            FirstPassViolations = firstPass,
            Violations = violations,
            Retries = retries,
            Completions = completions,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            RetriedForTruncation = retriedForTruncation,
        };
    }

    /// <summary>Fills a template's context and question placeholders.</summary>
    /// <param name="request">What the turn is asked to do.</param>
    /// <param name="context">The rendered context.</param>
    /// <returns>The prompt.</returns>
    private static string Render(TurnRequest request, string context) =>
        request.PromptTemplate
            .Replace("{context}", context, StringComparison.Ordinal)
            .Replace("{query}", request.Question, StringComparison.Ordinal);

    /// <summary>Builds the event that reports one completion, with what it cost.</summary>
    /// <param name="attempt">Which attempt it was.</param>
    /// <param name="model">The provider that answered.</param>
    /// <param name="answer">What it answered with.</param>
    /// <returns>The event.</returns>
    private static TurnCompleted Completed(int attempt, IModelProvider model, CompletionResponse answer) =>
        new(attempt, model.Id.Value, answer.Model, answer.Usage.InputTokens, answer.Usage.OutputTokens);

    private static async ValueTask<CompletionResponse> CompleteAsync(
        IModelProvider model,
        string modelId,
        string prompt,
        int budget,
        CancellationToken cancellationToken) =>
        await model
            .CompleteAsync(
                new CompletionRequest { Model = modelId, Prompt = prompt, MaxTokens = budget },
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Runs the enabled checks and returns the violations, prefixed by the check that found them.
    /// </summary>
    /// <remarks>
    /// The prefixes are the original's, and they are load-bearing: they are how a violation is routed back into the
    /// corrective prompt, so an answer that quotes badly is told about the quotes and not about the citations.
    /// </remarks>
    private static List<string> Check(
        string answer,
        TurnVerificationChecks checks,
        IReadOnlyList<string> servedTexts,
        IReadOnlyList<string> labels)
    {
        var violations = new List<string>();

        if (checks.Quotes)
        {
            violations.AddRange(
                TurnVerification.CheckQuotes(answer, servedTexts).Select(span => QuotePrefix + span));
        }

        if (checks.Citations)
        {
            violations.AddRange(
                TurnVerification.CheckCitations(answer, labels).Select(label => CitationPrefix + label));
        }

        return violations;
    }

    private static IReadOnlyList<string> QuotesOf(IReadOnlyList<string> violations) =>
        Of(violations, QuotePrefix);

    private static IReadOnlyList<string> CitationsOf(IReadOnlyList<string> violations) =>
        Of(violations, CitationPrefix);

    private static IReadOnlyList<string> Of(IReadOnlyList<string> violations, string prefix) =>
    [
        .. violations
            .Where(violation => violation.StartsWith(prefix, StringComparison.Ordinal))
            .Select(violation => violation[prefix.Length..]),
    ];
}
