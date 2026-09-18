namespace Munarium.Core.Tests.Support;

using Munarium.Evidence;

/// <summary>
/// Plans, layers and blocks a hierarchy test needs, so the two suites that run and compose them do not each
/// invent their own.
/// </summary>
public static class HierarchyFixture
{
    /// <summary>Builds a plan whose intent was supplied rather than modelled.</summary>
    /// <param name="layers">The layers, in execution order.</param>
    /// <returns>The plan.</returns>
    public static EvidencePlan Plan(params EvidenceLayer[] layers) => new()
    {
        Profile = "due-diligence",
        Intent = new QueryIntent
        {
            Question = "how many contracts lapse this quarter?",
            Kind = "aggregation",
            Explicit = true,
        },
        Layers = layers,
    };

    /// <summary>Builds one layer with a single pinned source.</summary>
    /// <param name="name">The layer's name.</param>
    /// <param name="source">The pinned source.</param>
    /// <param name="requirement">Whether its evidence is required.</param>
    /// <param name="role">The weight its evidence carries.</param>
    /// <param name="preserve">Whether a complete result has to survive composition intact.</param>
    /// <param name="charBudget">The layer's own character budget, when it has one.</param>
    /// <returns>The layer.</returns>
    public static EvidenceLayer Layer(
        string name,
        string source,
        LayerRequirement requirement,
        AnswerRole role,
        bool preserve = false,
        int? charBudget = null) => new()
        {
            Name = name,
            Sources = [source],
            Requirement = requirement,
            Role = role,
            PreserveCompleteResult = preserve,
            ContextCharBudget = charBudget,
        };

    /// <summary>Builds a table block.</summary>
    /// <param name="truncated">Whether rows were cut off.</param>
    /// <param name="rows">The rows, defaulting to one text row.</param>
    /// <param name="rowIds">The sealer's row ids, when it had any.</param>
    /// <param name="evidenceId">The sealed artifact, when there is one.</param>
    /// <returns>The block.</returns>
    public static EvidenceBlock Table(
        bool truncated,
        IReadOnlyList<IReadOnlyList<string?>>? rows = null,
        IReadOnlyList<string>? rowIds = null,
        string? evidenceId = "ev-1") => new TableBlock
        {
            Columns = ["region"],
            Rows = rows ?? [["r0"]],
            RowIds = rowIds ?? [],
            Truncated = truncated,
            EvidenceId = evidenceId,
        };

    /// <summary>Builds the refusal a source that could not be reached earns.</summary>
    /// <returns>The block.</returns>
    public static EvidenceBlock Unavailable() => new EvidenceRefusal
    {
        Code = EvidenceRefusalCodes.SourceUnavailable,
        Message = "the source could not be reached",
    };

    /// <summary>Builds a pinned slice of the ledger's own facts.</summary>
    /// <param name="keys">The keys to claim values for.</param>
    /// <returns>The block.</returns>
    public static EvidenceBlock Slice(params string[] keys) => new LedgerFactSlice(
    [
        .. keys.Select((key, index) => ClaimFixture.Create($"c{index}", index + 1, "service", key, $"v{index}")),
    ]);
}
