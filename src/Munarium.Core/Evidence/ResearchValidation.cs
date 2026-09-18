namespace Munarium.Evidence;

/// <summary>
/// What a profile got wrong.
/// </summary>
/// <remarks>
/// The fields are carried as well as the sentence: a runbook editor wants to point at the layer, a log wants to say
/// which profile, and the sentence is what an operator reads. <see cref="Detail"/> is the original's wording, because
/// that is the thing a person acts on.
/// </remarks>
public sealed record ResearchProblem
{
    /// <summary>Gets the sentence describing the problem.</summary>
    public required string Detail { get; init; }

    /// <summary>Gets the profile the problem is in, when it is in one.</summary>
    public string? Profile { get; init; }

    /// <summary>Gets the layer the problem is in, when it is in one.</summary>
    public string? Layer { get; init; }

    /// <summary>Gets the source the problem is about, when it is about one.</summary>
    public string? Source { get; init; }

    /// <summary>Gets the name the problem is about, when it is about a name rather than a source.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the declared size that could not fit, when that is the problem.</summary>
    public long? MaxBytes { get; init; }

    /// <summary>Gets the budget it could not fit, when that is the problem.</summary>
    public int? Budget { get; init; }
}

/// <summary>
/// Checks a profile before it is applied.
/// </summary>
/// <remarks>
/// Every check here is one that would otherwise fire mid-turn, in front of a user, with money already spent. A profile
/// naming a collection that does not exist is not a runtime condition to handle gracefully: it is a runbook that was
/// never correct, and the moment to say so is when someone applies it.
/// <para>
/// The sharpest one is the whole-or-nothing budget check. A layer can declare that its table has to survive composition
/// whole or not at all, because half a table is not a smaller true answer, it is a false one. If that layer is required
/// and the largest result it expects cannot fit the budget it will be composed under, then every turn the profile ever
/// serves has to refuse - a contradiction in the document, caught here rather than discovered one turn at a time.
/// </para>
/// <para>
/// The first violation is returned rather than all of them: the profile has to be rewritten either way, and a list of
/// findings hides which one made it unusable.
/// </para>
/// </remarks>
public static class ResearchValidation
{
    /// <summary>
    /// Checks the declarations a profile is built from.
    /// </summary>
    /// <param name="profiles">The declared profiles.</param>
    /// <param name="dataViews">The declared data views.</param>
    /// <param name="defaultProfile">The profile a turn runs under when it requests none, if any.</param>
    /// <param name="collections">The collections that exist, which is what a bare source name has to be one of.</param>
    /// <param name="contextCharBudget">The runbook's completion budget, when it declares one.</param>
    /// <returns>The first problem, or <see langword="null"/> when the document is coherent.</returns>
    public static ResearchProblem? Validate(
        IReadOnlyList<ResearchProfile> profiles,
        IReadOnlyList<DataViewDeclaration> dataViews,
        string? defaultProfile,
        IReadOnlyList<string> collections,
        int? contextCharBudget)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(dataViews);
        ArgumentNullException.ThrowIfNull(collections);

        var viewNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var view in dataViews)
        {
            if (!IsNameValid(view.Name))
            {
                return Malformed(view.Name);
            }

            if (!IsContractRefValid(view.Contract))
            {
                return new ResearchProblem
                {
                    Detail = $"data view '{view.Name}' names contract '{view.Contract}'; a contract ref is "
                        + "'name@version' with the name drawn from [A-Za-z0-9._-] and a numeric version",
                    Name = view.Name,
                };
            }

            if (!viewNames.Add(view.Name))
            {
                return new ResearchProblem { Detail = $"duplicate data view '{view.Name}'", Name = view.Name };
            }
        }

        return ProfilesOf(profiles, viewNames, defaultProfile, collections, contextCharBudget);
    }

    /// <summary>Checks the profiles themselves, and then each one's layers.</summary>
    private static ResearchProblem? ProfilesOf(
        IReadOnlyList<ResearchProfile> profiles,
        IReadOnlySet<string> viewNames,
        string? defaultProfile,
        IReadOnlyList<string> collections,
        int? contextCharBudget)
    {
        var profileNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var profile in profiles)
        {
            if (!IsNameValid(profile.Name))
            {
                return Malformed(profile.Name);
            }

            if (!profileNames.Add(profile.Name))
            {
                return new ResearchProblem
                {
                    Detail = $"duplicate research profile '{profile.Name}'",
                    Name = profile.Name,
                };
            }

            if (profile.Layers.Count == 0)
            {
                return new ResearchProblem
                {
                    Detail = $"research profile '{profile.Name}' declares no layers",
                    Profile = profile.Name,
                };
            }

            // A profile whose layers are all fallback never runs one: a fallback runs only when something before it
            // produced nothing, and nothing before it exists.
            if (profile.Layers.All(layer => layer.Requirement == LayerRequirement.Fallback))
            {
                return new ResearchProblem
                {
                    Detail = $"research profile '{profile.Name}' declares only fallback layers, so no layer ever runs",
                    Profile = profile.Name,
                };
            }

            if (LayersOf(profile, viewNames, collections, contextCharBudget) is { } problem)
            {
                return problem;
            }
        }

        return defaultProfile is { Length: > 0 } named && !profileNames.Contains(named)
            ? new ResearchProblem
            {
                Detail = $"retrieval.defaultResearchProfile '{named}' is not a declared profile",
                Name = named,
            }
            : null;
    }

    /// <summary>Checks one profile's layers: their names, their sources, and the whole-or-nothing budget.</summary>
    private static ResearchProblem? LayersOf(
        ResearchProfile profile,
        IReadOnlySet<string> viewNames,
        IReadOnlyList<string> collections,
        int? contextCharBudget)
    {
        var layerNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var layer in profile.Layers)
        {
            if (layer.Name.Trim().Length == 0)
            {
                return Malformed(layer.Name);
            }

            if (!layerNames.Add(layer.Name))
            {
                return new ResearchProblem
                {
                    Detail = $"profile '{profile.Name}' declares layer '{layer.Name}' twice",
                    Profile = profile.Name,
                    Layer = layer.Name,
                };
            }

            if (layer.Sources.Count == 0)
            {
                return new ResearchProblem
                {
                    Detail = $"profile '{profile.Name}' layer '{layer.Name}' names no sources; a layer that reads "
                        + "nothing cannot contribute evidence",
                    Profile = profile.Name,
                    Layer = layer.Name,
                };
            }

            var unknown = layer.Sources.FirstOrDefault(source => !IsKnownSource(source, viewNames, collections));

            if (unknown is not null)
            {
                return new ResearchProblem
                {
                    Detail = $"profile '{profile.Name}' layer '{layer.Name}' names source '{unknown}', which is not "
                        + "a declared collection, data view, or fact scope",
                    Profile = profile.Name,
                    Layer = layer.Name,
                    Source = unknown,
                };
            }

            if (layer.PreserveCompleteResult && BudgetProblemOf(profile, layer, contextCharBudget) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks a whole-or-nothing layer against the budget it will be composed under.
    /// </summary>
    /// <remarks>
    /// The layer's own budget wins if it declares one, else the profile's, else the runbook's. Only a required layer
    /// makes the mismatch fatal: an optional whole-or-nothing layer that does not fit simply contributes nothing, which
    /// is a legitimate design.
    /// </remarks>
    private static ResearchProblem? BudgetProblemOf(
        ResearchProfile profile,
        ResearchLayer layer,
        int? contextCharBudget)
    {
        if (layer.MaxBytes is not { } maxBytes)
        {
            return new ResearchProblem
            {
                Detail = $"profile '{profile.Name}' layer '{layer.Name}' sets preserveCompleteResult but no maxBytes, "
                    + "so its context budget cannot be checked before a turn runs",
                Profile = profile.Name,
                Layer = layer.Name,
            };
        }

        var budget = layer.ContextCharBudget ?? profile.ContextCharBudget ?? contextCharBudget;

        return budget is { } limit && layer.Requirement == LayerRequirement.Required && maxBytes > limit
            ? new ResearchProblem
            {
                Detail = $"profile '{profile.Name}' layer '{layer.Name}' is required and preserves its complete "
                    + $"result, but its maxBytes {maxBytes} cannot fit completion.contextCharBudget {limit}; every "
                    + "turn using this profile would refuse",
                Profile = profile.Name,
                Layer = layer.Name,
                MaxBytes = maxBytes,
                Budget = limit,
            }
            : null;
    }

    private static ResearchProblem Malformed(string name) => new()
    {
        Detail = $"name '{name}' must be non-empty and must not contain whitespace or ':'",
        Name = name,
    };

    /// <summary>
    /// Gets a value indicating whether a name can appear in a profile.
    /// </summary>
    /// <remarks>
    /// A colon is what a source prefix is spelled with, so a name containing one would make source resolution
    /// ambiguous: <c>matrix:x</c> is how a layer names a view, and a view named <c>matrix:x</c> could not be told apart
    /// from it.
    /// </remarks>
    private static bool IsNameValid(string name) =>
        name.Trim().Length > 0 && !name.Contains(':', StringComparison.Ordinal) && !name.Any(char.IsWhiteSpace);

    /// <summary>
    /// Gets a value indicating whether a contract ref is a path-safe <c>name@version</c>.
    /// </summary>
    /// <remarks>
    /// The ref is spliced into <c>{base}/v1/{route}/{contract}/execute</c>, so <c>..</c>, <c>/</c>, <c>?</c> and
    /// <c>#</c> are exactly the characters that must never appear in it.
    /// </remarks>
    private static bool IsContractRefValid(string contract)
    {
        var at = contract.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0 || at == contract.Length - 1)
        {
            return false;
        }

        var name = contract[..at];
        var version = contract[(at + 1)..];

        return name is not ("." or "..")
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            && version.All(char.IsAsciiDigit);
    }

    private static bool IsKnownSource(string source, IReadOnlySet<string> viewNames, IReadOnlyList<string> collections) =>
        IsKnownPrefixSource(source, viewNames) || collections.Contains(source, StringComparer.Ordinal);

    /// <summary>Checks a prefix-qualified source: a data view, a fact version or a scope prefix.</summary>
    private static bool IsKnownPrefixSource(string source, IReadOnlySet<string> viewNames) =>
        source.StartsWith(EvidencePlanes.MatrixPrefix, StringComparison.Ordinal)
            ? viewNames.Contains(source[EvidencePlanes.MatrixPrefix.Length..])
            : IsNamedPrefixSource(source);

    /// <summary>
    /// Checks a <c>facts:</c> or <c>scope:</c> source names something after its prefix.
    /// </summary>
    /// <remarks>
    /// A fact layer has to name the memory version it reads. A session carries no version binding today, so a bare
    /// <c>facts</c> could only ever refuse at turn time - and a runbook that validates and then refuses every turn is
    /// the vacuous-green trap in reverse, not a design.
    /// </remarks>
    private static bool IsNamedPrefixSource(string source) =>
        (source.StartsWith(EvidencePlanes.FactsPrefix, StringComparison.Ordinal)
            || source.StartsWith(EvidencePlanes.ScopePrefix, StringComparison.Ordinal))
        && source.Length > source.IndexOf(':', StringComparison.Ordinal) + 1;
}
