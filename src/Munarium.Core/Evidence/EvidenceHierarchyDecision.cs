namespace Munarium.Evidence;

/// <summary>
/// One layer's outcome, as recorded on the turn.
/// </summary>
public sealed record LayerOutcome
{
    /// <summary>Gets the layer's name.</summary>
    public required string Layer { get; init; }

    /// <summary>Gets the weight the layer's evidence carries.</summary>
    public required AnswerRole Role { get; init; }

    /// <summary>Gets whether the layer's evidence was required.</summary>
    public required LayerRequirement Requirement { get; init; }

    /// <summary>Gets the block's kind, or <c>refusal</c>.</summary>
    public required string Block { get; init; }

    /// <summary>Gets the sealed artifact the layer contributed, when it contributed one.</summary>
    public string? EvidenceId { get; init; }

    /// <summary>Gets a value indicating whether the block could support a completeness claim.</summary>
    public required bool SupportsCompleteness { get; init; }

    /// <summary>Gets the refusal's code, when the layer declined.</summary>
    public string? RefusalCode { get; init; }

    /// <summary>Gets how long the layer took.</summary>
    public required long ElapsedMilliseconds { get; init; }
}

/// <summary>
/// What the hierarchy actually did, persisted per turn.
/// </summary>
/// <remarks>
/// This is the audit answer to "why did the model see what it saw?", and it is deliberately about the
/// <em>decision</em> rather than the content: which profile, which pinned sources, which layers ran, which refused,
/// whether any completeness claim was permissible. Evidence rows never appear here.
/// </remarks>
public sealed record EvidenceHierarchyDecision
{
    /// <summary>Gets the research profile that was applied.</summary>
    public required string Profile { get; init; }

    /// <summary>Gets the intent's kind, when it had one.</summary>
    public string? IntentKind { get; init; }

    /// <summary>Gets a value indicating whether the intent was supplied rather than modelled.</summary>
    public required bool IntentExplicit { get; init; }

    /// <summary>Gets the layers' outcomes, in execution order.</summary>
    public required IReadOnlyList<LayerOutcome> Layers { get; init; }

    /// <summary>Gets a value indicating whether any block could support a completeness claim.</summary>
    public required bool CompletenessAvailable { get; init; }

    /// <summary>Gets how many conflicts between layers were detected and preserved.</summary>
    public int DisclosedConflicts { get; init; }

    /// <summary>Gets the conflict policy that was applied.</summary>
    public required string ConflictsPolicy { get; init; }

    /// <summary>
    /// Finds the required layer that failed to produce evidence, when one did.
    /// </summary>
    /// <remarks>
    /// The turn has to refuse when this returns something - that is what <c>required</c> means. And the refusal
    /// must not name the layer's sources: a caller who cannot see a source must not learn of it from the shape of
    /// a refusal, which is the hidden-required-layer rule.
    /// </remarks>
    /// <returns>The failed layer's outcome, or <see langword="null"/> when every required layer answered.</returns>
    public LayerOutcome? RequiredLayerFailed() => Layers.FirstOrDefault(outcome =>
        outcome.Requirement is LayerRequirement.Required && outcome.Block is "refusal");
}
