namespace Munarium.Budgets;

/// <summary>
/// The per-call output-token ceilings a deployment hands a model provider.
/// </summary>
/// <remarks>
/// One object rather than eight scattered constants, because a replacement replaces the <em>whole</em> set: a body
/// missing a field is invalid input rather than a partial update, so an operator cannot change a ceiling by accident
/// while leaving the others at whatever they were.
/// <para>
/// The built-ins are the original's, and the environment overrides them per variable. Where a runbook declares its own
/// ceiling, the runbook wins - it is the document that pays for the call.
/// </para>
/// </remarks>
/// <param name="TurnCompletion">A session turn's answer, which a runbook's <c>completion.maxTokens</c> overrides.</param>
/// <param name="QueryExpansion">The <c>modelQueryExpansion</c> variant call, which a runbook overrides too.</param>
/// <param name="CompleteDefault">What a relayed completion gets when the caller names no ceiling.</param>
/// <param name="HealthAiProbe">Each probe completion the plane's health check makes.</param>
/// <param name="HierarchyClassifier">The one-word question classifier the evidence hierarchy asks.</param>
/// <param name="HierarchyIntent">
/// The semantic-intent task. Settable and carried, and read by nothing in this port yet, because its evidence hierarchy
/// resolves intent in one classification call where the original makes two.
/// </param>
/// <param name="RunbookAdvisory">The advisory pass a runbook validation may ask a model for.</param>
/// <param name="AuthoringAssist">The guided-authoring assist draft.</param>
public sealed record MaxTokensBudget(
    int TurnCompletion,
    int QueryExpansion,
    int CompleteDefault,
    int HealthAiProbe,
    int HierarchyClassifier,
    int HierarchyIntent,
    int RunbookAdvisory,
    int AuthoringAssist)
{
    /// <summary>The ceilings a deployment gets when nothing is set: the original's own built-ins.</summary>
    public static MaxTokensBudget Builtin { get; } = new(
        TurnCompletion: 2048,
        QueryExpansion: 256,
        CompleteDefault: 1024,
        HealthAiProbe: 512,
        HierarchyClassifier: 32,
        HierarchyIntent: 480,
        RunbookAdvisory: 2048,
        AuthoringAssist: 8192);

    /// <summary>The name a ceiling is spelled with, and the process variable that overrides it.</summary>
    /// <param name="Name">The name the contract carries.</param>
    /// <param name="EnvironmentVariable">The variable that overrides it.</param>
    public readonly record struct Ceiling(string Name, string EnvironmentVariable);

    /// <summary>
    /// Every ceiling, in the order the contract names them.
    /// </summary>
    /// <remarks>
    /// A list rather than eight separate reads, so that the names the wire carries and the variables a process reads can
    /// be checked against each other in one place.
    /// </remarks>
    public static readonly IReadOnlyList<Ceiling> All =
    [
        new("turn_completion", "MUNARIUM_MAX_TOKENS_TURN_COMPLETION"),
        new("query_expansion", "MUNARIUM_MAX_TOKENS_QUERY_EXPANSION"),
        new("complete_default", "MUNARIUM_MAX_TOKENS_COMPLETE_DEFAULT"),
        new("healthai_probe", "MUNARIUM_MAX_TOKENS_HEALTHAI_PROBE"),
        new("hierarchy_classifier", "MUNARIUM_MAX_TOKENS_HIERARCHY_CLASSIFIER"),
        new("hierarchy_intent", "MUNARIUM_MAX_TOKENS_HIERARCHY_INTENT"),
        new("runbook_advisory", "MUNARIUM_MAX_TOKENS_RUNBOOK_ADVISORY"),
        new("authoring_assist", "MUNARIUM_MAX_TOKENS_AUTHORING_ASSIST"),
    ];

    /// <summary>
    /// Refuses a set of ceilings that could not be honoured, naming which one and why.
    /// </summary>
    /// <remarks>
    /// The original's own ranges, ported: a turn's answer has a floor, and a ceiling a truncation retry could pay four
    /// times over; an expansion is a short list rather than an essay; and the rest are bounded only by what a provider
    /// accepts.
    /// </remarks>
    /// <param name="budget">The ceilings.</param>
    /// <returns>The reason, or <see langword="null"/> when every one is usable.</returns>
    public static string? Refusal(MaxTokensBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);

        return Refusals(budget).FirstOrDefault(reason => reason is not null);
    }

    /// <summary>One entry per range rule, in the order an operator would fix them.</summary>
    /// <param name="budget">The ceilings.</param>
    /// <returns>The reason each rule gives, or nothing when it holds.</returns>
    private static IEnumerable<string?> Refusals(MaxTokensBudget budget) =>
    [
        Range("turn_completion", budget.TurnCompletion, 256, 16_384),
        Range("query_expansion", budget.QueryExpansion, 32, 512),
        Range("complete_default", budget.CompleteDefault, 1, 65_536),
        Range("healthai_probe", budget.HealthAiProbe, 1, 65_536),
        Range("hierarchy_classifier", budget.HierarchyClassifier, 1, 65_536),
        Range("hierarchy_intent", budget.HierarchyIntent, 1, 65_536),
        Range("runbook_advisory", budget.RunbookAdvisory, 1, 65_536),
        Range("authoring_assist", budget.AuthoringAssist, 1, 65_536),
    ];

    /// <summary>Reads one ceiling against its range.</summary>
    /// <param name="name">The ceiling's name.</param>
    /// <param name="value">Its value.</param>
    /// <param name="lowest">The lowest value it may carry.</param>
    /// <param name="highest">The highest value it may carry.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    private static string? Range(string name, int value, int lowest, int highest) =>
        value < lowest || value > highest
            ? $"{name} must be between {lowest} and {highest}, and it is {value}"
            : null;

    /// <summary>
    /// The built-ins with every set variable laid over them.
    /// </summary>
    /// <param name="baseBudget">What to start from, normally the built-ins.</param>
    /// <param name="environment">Reads an environment variable, or answers <see langword="null"/>.</param>
    /// <returns>The ceilings a process is composed with.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a variable is set to something that is not a usable ceiling: a deployment that cannot say what its
    /// ceilings are should not serve paid calls, and quietly keeping the built-in would hide the typo that set it.
    /// </exception>
    public static MaxTokensBudget Between(MaxTokensBudget baseBudget, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(baseBudget);
        ArgumentNullException.ThrowIfNull(environment);

        var named = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["turn_completion"] = baseBudget.TurnCompletion,
            ["query_expansion"] = baseBudget.QueryExpansion,
            ["complete_default"] = baseBudget.CompleteDefault,
            ["healthai_probe"] = baseBudget.HealthAiProbe,
            ["hierarchy_classifier"] = baseBudget.HierarchyClassifier,
            ["hierarchy_intent"] = baseBudget.HierarchyIntent,
            ["runbook_advisory"] = baseBudget.RunbookAdvisory,
            ["authoring_assist"] = baseBudget.AuthoringAssist,
        };

        foreach (var ceiling in All)
        {
            var value = environment(ceiling.EnvironmentVariable);

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            named[ceiling.Name] = int.TryParse(
                value.Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"{ceiling.EnvironmentVariable} must be an integer, and it is set to '{value.Trim()}'");
        }

        var budget = new MaxTokensBudget(
            named["turn_completion"],
            named["query_expansion"],
            named["complete_default"],
            named["healthai_probe"],
            named["hierarchy_classifier"],
            named["hierarchy_intent"],
            named["runbook_advisory"],
            named["authoring_assist"]);

        return Refusal(budget) is { } reason
            ? throw new InvalidOperationException($"the ceilings this process is composed with are unusable: {reason}")
            : budget;
    }
}
