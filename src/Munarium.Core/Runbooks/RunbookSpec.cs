namespace Munarium.Runbooks;

using Munarium.Evidence;

/// <summary>
/// A runbook's body: what it reads, how it retrieves, and how it runs.
/// </summary>
/// <remarks>
/// A v1 document names a single <see cref="Shape"/>; a v2 document declares <see cref="Collections"/> instead. Both are
/// carried, because a v1 document is still a valid document - what changes is which of the two the executor walks.
/// </remarks>
public sealed record RunbookSpec
{
    /// <summary>Gets the shape a v1 document operates on, as <c>name@version</c>.</summary>
    public string? Shape { get; init; }

    /// <summary>Gets the collections a v2 document spans.</summary>
    public IReadOnlyList<CollectionSpec> Collections { get; init; } = [];

    /// <summary>Gets the retrieval knobs.</summary>
    public RetrievalSpec? Retrieval { get; init; }

    /// <summary>
    /// Gets the query contracts this runbook may read.
    /// </summary>
    /// <remarks>
    /// A layer names one as <c>matrix:&lt;name&gt;</c>, and the model never writes SQL.
    /// </remarks>
    public IReadOnlyList<DataViewDeclaration> DataViews { get; init; } = [];

    /// <summary>Gets the per-level model defaults and the API-override policy.</summary>
    public ModelsSpec? Models { get; init; }

    /// <summary>Gets the optional completion for session turns.</summary>
    public CompletionSpec? Completion { get; init; }

    /// <summary>Gets where this runbook's documents live in object storage.</summary>
    public SourcesSpec? Sources { get; init; }

    /// <summary>Gets how the run plan is flattened across collections.</summary>
    public ExecutionSpec? Execution { get; init; }

    /// <summary>
    /// Gets the steps, in execution order.
    /// </summary>
    /// <remarks>
    /// The original reads these as raw single-key maps and converts them, because its YAML library dropped the
    /// single-key-map enum syntax; this port reads them straight into <see cref="RunbookStep"/>, so there is no raw form
    /// to keep.
    /// </remarks>
    public IReadOnlyList<RunbookStep> Steps { get; init; } = [];

    /// <summary>Gets a value indicating whether this is a v2 document, which is to say it declares collections.</summary>
    public bool IsV2 => Collections.Count > 0;

    /// <summary>
    /// Gets the collections this runbook spans, with a v1 document normalized to one implicit level-0 collection.
    /// </summary>
    /// <remarks>
    /// The normalization is for display and information only: a v1 document's execution stays on the shape-scoped path
    /// it has always taken, so this cannot change what a v1 run does.
    /// </remarks>
    public IReadOnlyList<CollectionSpec> EffectiveCollections
    {
        get
        {
            if (IsV2)
            {
                return Collections;
            }

            if (Shape is not { Length: > 0 } shapeRef)
            {
                return [];
            }

            var at = shapeRef.IndexOf('@', StringComparison.Ordinal);

            return
            [
                new CollectionSpec
                {
                    Name = at > 0 ? shapeRef[..at] : shapeRef,
                    Shape = shapeRef,
                },
            ];
        }
    }

    /// <summary>Gets the declared plan-flattening order, which defaults to step-major.</summary>
    public ExecutionOrder ExecutionOrderOf() => Execution?.Order ?? ExecutionOrder.StepMajor;
}
