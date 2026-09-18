namespace Munarium.Evidence;

/// <summary>
/// One bound contract parameter.
/// </summary>
/// <remarks>
/// <see cref="Type"/> is the contract's declared type (<c>date</c>, <c>string</c>, <c>int64</c>, <c>decimal</c>, …) and
/// <see cref="Value"/> is always text: a decimal parameter that round-tripped through a JSON number would arrive at the
/// source having lost the precision the contract was written to keep.
/// </remarks>
public sealed record DataViewParameter
{
    /// <summary>Gets the type the contract declares for this parameter.</summary>
    public required string Type { get; init; }

    /// <summary>Gets the value, as text.</summary>
    public required string Value { get; init; }
}

/// <summary>
/// A pre-declared query contract a profile may read.
/// </summary>
/// <remarks>
/// The model never writes SQL. A turn selects a data view by name from this list, and the plane executes the contract
/// that name is bound to - so the injection surface is not defended against, it does not exist.
/// </remarks>
public sealed record DataViewDeclaration
{
    /// <summary>Gets the name a layer refers to this view by, as <c>matrix:&lt;name&gt;</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the contract, as <c>name@version</c>.
    /// </summary>
    /// <remarks>
    /// Pinned: a contract that changed under a profile is a different question being answered.
    /// </remarks>
    public required string Contract { get; init; }

    /// <summary>Gets what kind of asset the view binds, which decides the route and whether the intent is semantic.</summary>
    public DataViewKind Kind { get; init; } = DataViewKind.Contract;

    /// <summary>Gets the description surfaced to the intent resolver, so it can choose between views.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the values of the contract's declared parameters, by parameter name.
    /// </summary>
    /// <remarks>
    /// Bound in the profile, never at turn time. A contract with a required parameter is unreachable without this - and
    /// letting a turn supply one would hand the caller a knob on a query the whole point of which is that it was
    /// declared in advance.
    /// </remarks>
    public IReadOnlyDictionary<string, DataViewParameter> Parameters { get; init; } =
        new SortedDictionary<string, DataViewParameter>(StringComparer.Ordinal);

    /// <summary>Gets the access level a session has to dominate to read this view.</summary>
    public int AccessLevel { get; init; }

    /// <summary>Gets the need-to-know tags a session has to carry, all of them.</summary>
    public IReadOnlyList<string> Compartments { get; init; } = [];
}

/// <summary>
/// One layer of a research profile.
/// </summary>
public sealed record ResearchLayer
{
    /// <summary>Gets the layer's stable name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the pinned sources: a collection name, <c>facts:&lt;version&gt;</c>, <c>scope:&lt;prefix&gt;</c>, or
    /// <c>matrix:&lt;view&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Resolved when the profile is applied, never during a turn, so a turn cannot widen its own reach by naming
    /// something new.
    /// </remarks>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Gets whether the layer's evidence is required for the answer to stand.</summary>
    public LayerRequirement Requirement { get; init; } = LayerRequirement.Optional;

    /// <summary>Gets the weight the layer's evidence carries.</summary>
    public AnswerRole Role { get; init; } = AnswerRole.Primary;

    /// <summary>Gets the layer's own character budget, when it declares one.</summary>
    public int? ContextCharBudget { get; init; }

    /// <summary>Gets a value indicating whether the layer's result has to survive composition whole or not be used.</summary>
    public bool PreserveCompleteResult { get; init; }

    /// <summary>
    /// Gets the largest this layer's result is expected to be.
    /// </summary>
    /// <remarks>
    /// Only meaningful beside <see cref="PreserveCompleteResult"/>, where it is what makes the budget contradiction
    /// checkable when the profile is applied instead of one turn at a time.
    /// </remarks>
    public long? MaxBytes { get; init; }

    /// <summary>Gets how long the layer may take before it is abandoned.</summary>
    public long? DeadlineMilliseconds { get; init; }
}

/// <summary>
/// An ordered hierarchy of evidence layers.
/// </summary>
/// <remarks>
/// A profile says what evidence exists and in what order it is trusted; the runner says what a layer's output can be
/// used for. Nothing here executes anything.
/// </remarks>
public sealed record ResearchProfile
{
    /// <summary>Gets the profile's name, which a turn requests by.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the description a caller sees when choosing a profile.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the layers, in execution order - which <em>is</em> the hierarchy.</summary>
    public IReadOnlyList<ResearchLayer> Layers { get; init; } = [];

    /// <summary>Gets the profile's character budget, which a layer's own overrides.</summary>
    public int? ContextCharBudget { get; init; }
}

/// <summary>
/// Resolving a profile into the plan a runner executes.
/// </summary>
public static class ResearchProfiles
{
    /// <summary>
    /// Resolves which profile a turn runs under.
    /// </summary>
    /// <remarks>
    /// Fails closed on a named-but-undeclared profile: silently falling back to the document path would answer a
    /// different question than the caller asked, and would do it invisibly.
    /// </remarks>
    /// <param name="profiles">The declared profiles.</param>
    /// <param name="requested">The profile the caller named, if any.</param>
    /// <param name="defaultProfile">The profile a turn runs under when it names none, if any.</param>
    /// <returns>The profile to run, or why there is none to run.</returns>
    public static (ResearchProfile? Profile, ResearchProblem? Problem) Resolve(
        IReadOnlyList<ResearchProfile> profiles,
        string? requested,
        string? defaultProfile)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        // No request and no default: the document path, exactly as any deployment without profiles answers.
        var name = requested is { Length: > 0 } ? requested : defaultProfile;

        if (name is not { Length: > 0 })
        {
            return (null, null);
        }

        var found = profiles.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.Ordinal));

        return found is null
            ? (null, new ResearchProblem { Detail = $"unknown research profile '{name}'", Name = name })
            : (found, null);
    }

    /// <summary>
    /// Turns a declared profile into an executable plan.
    /// </summary>
    /// <remarks>
    /// The layers keep their declared order, because the order <em>is</em> the hierarchy: earlier layers outrank later
    /// ones when composition has to choose. Nothing is resolved here that a turn could then widen - the sources are the
    /// profile's own, pinned when it was applied.
    /// </remarks>
    /// <param name="profile">The profile to run.</param>
    /// <param name="intent">What the turn intends to find out.</param>
    /// <returns>The plan.</returns>
    public static EvidencePlan BuildPlan(ResearchProfile profile, QueryIntent intent)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(intent);

        return new EvidencePlan
        {
            Profile = profile.Name,
            Intent = intent,
            Layers =
            [
                .. profile.Layers.Select(layer => new EvidenceLayer
                {
                    Name = layer.Name,
                    Sources = layer.Sources,
                    Requirement = layer.Requirement,
                    Role = layer.Role,
                    ContextCharBudget = layer.ContextCharBudget,
                    PreserveCompleteResult = layer.PreserveCompleteResult,
                    DeadlineMilliseconds = layer.DeadlineMilliseconds,
                }),
            ],
            ContextCharBudget = profile.ContextCharBudget,
        };
    }
}
