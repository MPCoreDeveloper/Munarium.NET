namespace Munarium.Evidence;

using Munarium.Retrieval;

/// <summary>
/// What one execution of a turn is given to work with.
/// </summary>
/// <remarks>
/// Gathered rather than passed one at a time, because these are one decision - which plan runs, what the turn is asked
/// for, where its evidence comes from, and which model answers over it - and a turn that disagreed with itself about any
/// of them would be running a different turn than the one it was asked for.
/// </remarks>
public sealed record TurnExecution
{
    /// <summary>Gets the plan to execute.</summary>
    public required EvidencePlan Plan { get; init; }

    /// <summary>Gets what the turn is asked to do.</summary>
    public required TurnRequest Request { get; init; }

    /// <summary>Gets the evidence providers, in trust order.</summary>
    public required IReadOnlyList<IEvidenceProvider> Providers { get; init; }

    /// <summary>Gets the delegate that runs the document path for a layer.</summary>
    public required Func<EvidenceLayer, CancellationToken, ValueTask<RetrievalResult>> DocumentLayer { get; init; }

    /// <summary>Gets the delegate that reads the citable labels out of a document retrieval.</summary>
    public required Func<RetrievalResult, IReadOnlyList<string>> ServedLabels { get; init; }

    /// <summary>Gets the model to answer with.</summary>
    public required AnsweringModel Model { get; init; }

    /// <summary>Gets how many characters of composed context the prompt may carry.</summary>
    public int ContextBudget { get; init; } = TurnPipeline.DefaultContextBudget;

    /// <summary>Gets an optional listener; the turn's result never depends on one being present.</summary>
    public Action<TurnProgress>? OnProgress { get; init; }
}
