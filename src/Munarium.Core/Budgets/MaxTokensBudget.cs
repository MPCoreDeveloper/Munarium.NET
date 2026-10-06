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
    /// The names the contract carries, one per ceiling, written once.
    /// </summary>
    /// <remarks>
    /// Each name is read in four places - the list a deployment iterates, the range rule that refuses an unusable one, the
    /// read that applies a replacement over the built-ins, and the read that puts a replacement back into names - so a
    /// literal spelled four times is a name that can be misspelled in one of them and nowhere else.
    /// </remarks>
    private static class Names
    {
        internal const string TurnCompletion = "turn_completion";
        internal const string QueryExpansion = "query_expansion";
        internal const string CompleteDefault = "complete_default";
        internal const string HealthAiProbe = "healthai_probe";
        internal const string HierarchyClassifier = "hierarchy_classifier";
        internal const string HierarchyIntent = "hierarchy_intent";
        internal const string RunbookAdvisory = "runbook_advisory";
        internal const string AuthoringAssist = "authoring_assist";
    }

    /// <summary>
    /// Every ceiling, in the order the contract names them.
    /// </summary>
    /// <remarks>
    /// A list rather than eight separate reads, so that the names the wire carries and the variables a process reads can
    /// be checked against each other in one place.
    /// </remarks>
    public static readonly IReadOnlyList<Ceiling> All =
    [
        new(Names.TurnCompletion, "MUNARIUM_MAX_TOKENS_TURN_COMPLETION"),
        new(Names.QueryExpansion, "MUNARIUM_MAX_TOKENS_QUERY_EXPANSION"),
        new(Names.CompleteDefault, "MUNARIUM_MAX_TOKENS_COMPLETE_DEFAULT"),
        new(Names.HealthAiProbe, "MUNARIUM_MAX_TOKENS_HEALTHAI_PROBE"),
        new(Names.HierarchyClassifier, "MUNARIUM_MAX_TOKENS_HIERARCHY_CLASSIFIER"),
        new(Names.HierarchyIntent, "MUNARIUM_MAX_TOKENS_HIERARCHY_INTENT"),
        new(Names.RunbookAdvisory, "MUNARIUM_MAX_TOKENS_RUNBOOK_ADVISORY"),
        new(Names.AuthoringAssist, "MUNARIUM_MAX_TOKENS_AUTHORING_ASSIST"),
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
        Range(Names.TurnCompletion, budget.TurnCompletion, 256, 16_384),
        Range(Names.QueryExpansion, budget.QueryExpansion, 32, 512),
        Range(Names.CompleteDefault, budget.CompleteDefault, 1, 65_536),
        Range(Names.HealthAiProbe, budget.HealthAiProbe, 1, 65_536),
        Range(Names.HierarchyClassifier, budget.HierarchyClassifier, 1, 65_536),
        Range(Names.HierarchyIntent, budget.HierarchyIntent, 1, 65_536),
        Range(Names.RunbookAdvisory, budget.RunbookAdvisory, 1, 65_536),
        Range(Names.AuthoringAssist, budget.AuthoringAssist, 1, 65_536),
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
            [Names.TurnCompletion] = baseBudget.TurnCompletion,
            [Names.QueryExpansion] = baseBudget.QueryExpansion,
            [Names.CompleteDefault] = baseBudget.CompleteDefault,
            [Names.HealthAiProbe] = baseBudget.HealthAiProbe,
            [Names.HierarchyClassifier] = baseBudget.HierarchyClassifier,
            [Names.HierarchyIntent] = baseBudget.HierarchyIntent,
            [Names.RunbookAdvisory] = baseBudget.RunbookAdvisory,
            [Names.AuthoringAssist] = baseBudget.AuthoringAssist,
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
            named[Names.TurnCompletion],
            named[Names.QueryExpansion],
            named[Names.CompleteDefault],
            named[Names.HealthAiProbe],
            named[Names.HierarchyClassifier],
            named[Names.HierarchyIntent],
            named[Names.RunbookAdvisory],
            named[Names.AuthoringAssist]);

        return Refusal(budget) is { } reason
            ? throw new InvalidOperationException($"the ceilings this process is composed with are unusable: {reason}")
            : budget;
    }
}
