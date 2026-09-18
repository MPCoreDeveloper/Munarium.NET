namespace Munarium.Evidence;

/// <summary>
/// What a turn intends to find out.
/// </summary>
/// <remarks>
/// Produced by a model resolver under the intent task, never by the caller - except when a caller supplies one,
/// which is why <see cref="Explicit"/> exists: compose conformance has to run without a model call, and recording
/// which of the two happened keeps a measured result honest about whether a planner was involved.
/// </remarks>
public sealed record QueryIntent
{
    /// <summary>Gets the question as asked.</summary>
    public required string Question { get; init; }

    /// <summary>Gets the free-form intent kind, such as <c>lookup</c>, <c>aggregation</c> or <c>comparison</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>Gets a value indicating whether the intent was supplied rather than modelled.</summary>
    public bool Explicit { get; init; }

    /// <summary>Gets what the intent task chose to ask each semantic data view, keyed by its name.</summary>
    public IReadOnlyDictionary<string, SemanticSelection> Selections { get; init; } =
        new SortedDictionary<string, SemanticSelection>(StringComparer.Ordinal);
}

/// <summary>
/// One data view's semantic selection: names only, never SQL.
/// </summary>
public sealed record SemanticSelection
{
    /// <summary>Gets the measures to aggregate.</summary>
    public IReadOnlyList<string> Measures { get; init; } = [];

    /// <summary>Gets the dimensions to group by.</summary>
    public IReadOnlyList<string> Dimensions { get; init; } = [];

    /// <summary>Gets the equality filters to apply.</summary>
    public IReadOnlyList<SemanticFilterSelection> Filters { get; init; } = [];
}

/// <summary>
/// One equality filter on a semantic view.
/// </summary>
/// <param name="Dimension">The dimension to filter on.</param>
/// <param name="Value">The value to match.</param>
public sealed record SemanticFilterSelection(string Dimension, string Value)
{
    /// <summary>Gets the dimension's declared type, carried so the value is bound as it rather than as text.</summary>
    public string Type { get; init; } = "string";
}

/// <summary>
/// The resolved plan for one turn: which layers, in what order, under what budgets.
/// </summary>
public sealed record EvidencePlan
{
    /// <summary>Gets the research profile the plan came from.</summary>
    public required string Profile { get; init; }

    /// <summary>Gets what the turn intends to find out.</summary>
    public required QueryIntent Intent { get; init; }

    /// <summary>
    /// Gets the layers, in execution order.
    /// </summary>
    /// <remarks>
    /// The order <em>is</em> the hierarchy: earlier layers outrank later ones when composition has to choose.
    /// </remarks>
    public required IReadOnlyList<EvidenceLayer> Layers { get; init; }

    /// <summary>Gets the total character budget across every layer.</summary>
    public int? ContextCharBudget { get; init; }

    /// <summary>
    /// Gets the policy for a conflict between layers.
    /// </summary>
    /// <remarks>
    /// The one policy this contract version implements is <c>preserve_and_disclose</c>: a conflict between layers
    /// is preserved and disclosed, never silently resolved in favour of the higher one. The field exists so that
    /// an alternative would be a visible change rather than a quiet one.
    /// </remarks>
    public string Conflicts { get; init; } = PreserveAndDisclose;

    /// <summary>The policy name: conflicts are preserved and disclosed.</summary>
    public const string PreserveAndDisclose = "preserve_and_disclose";
}
