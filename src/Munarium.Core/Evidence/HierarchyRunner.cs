namespace Munarium.Evidence;

using System.Diagnostics;
using Munarium.Retrieval;

/// <summary>
/// What one layer contributed.
/// </summary>
/// <param name="Layer">The layer's name, and the key the decision and the composer both use.</param>
/// <param name="Block">What the layer produced.</param>
public sealed record LayerBlock(string Layer, EvidenceBlock Block);

/// <summary>
/// What a hierarchy run produced.
/// </summary>
public sealed record HierarchyOutcome
{
    /// <summary>Gets what the hierarchy did, which is the audit answer to why a model saw what it saw.</summary>
    public required EvidenceHierarchyDecision Decision { get; init; }

    /// <summary>Gets the blocks, in execution order.</summary>
    public required IReadOnlyList<LayerBlock> Blocks { get; init; }

    /// <summary>
    /// Gets the document layer's full retrieval, when one ran.
    /// </summary>
    /// <remarks>
    /// Captured rather than flattened into a block like every other layer, and honestly so: the response contract
    /// is document-shaped for backward compatibility, so pretending all layers are alike and then reconstructing
    /// the document fields from a generic block would be architecture theatre.
    /// </remarks>
    public RetrievalResult? Documents { get; init; }
}

/// <summary>
/// A required layer produced no evidence, so the turn refuses.
/// </summary>
/// <remarks>
/// The refusal names the <em>layer</em>, never its sources: a caller who cannot see a source must not learn of it
/// from the shape of a refusal, which is the hidden-required-layer rule.
/// </remarks>
/// <param name="Layer">The layer that failed.</param>
/// <param name="RefusalCode">The code the layer refused with.</param>
public sealed record RequiredLayerUnavailable(string Layer, string RefusalCode);

/// <summary>
/// The result of running a plan: what it produced, or the refusal it earned.
/// </summary>
/// <remarks>
/// A union rather than a thrown exception, because a required layer that could not answer is an outcome of a turn
/// - something the caller has to disclose - and not a bug. A caller cannot forget it either: the union makes the
/// refusal a case that has to be handled.
/// </remarks>
public readonly union HierarchyResult(HierarchyOutcome, RequiredLayerUnavailable);

/// <summary>
/// Runs an evidence plan's layers in order.
/// </summary>
/// <remarks>
/// Execution order <em>is</em> the hierarchy: earlier layers outrank later ones when composition has to choose.
/// A fallback layer runs only when nothing before it produced evidence, so it is a last resort rather than another
/// opinion.
/// <para>
/// Providers are tried in order for each source and the first that claims it wins. A bare source name is a
/// collection served by the document path; a plane-qualified one that no provider claims makes the layer refuse.
/// </para>
/// <para>
/// The runner never enforces <see cref="EvidenceLayer.DeadlineMilliseconds"/> itself: the deadline travels with
/// the layer to the provider, which is the only place that can turn a slow source into a <c>source-timeout</c>
/// refusal rather than a cancelled turn.
/// </para>
/// </remarks>
public static class HierarchyRunner
{
    /// <summary>
    /// Executes a plan.
    /// </summary>
    /// <param name="plan">The plan to execute.</param>
    /// <param name="providers">The providers to try, in order, for each of a layer's pinned sources.</param>
    /// <param name="documentLayer">Runs the document path for a layer, and returns its full retrieval.</param>
    /// <param name="onProgress">An optional listener; the run's result never depends on one being present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the run produced, or the refusal a required layer earned.</returns>
    public static async ValueTask<HierarchyResult> ExecuteAsync(
        EvidencePlan plan,
        IReadOnlyList<IEvidenceProvider> providers,
        Func<EvidenceLayer, CancellationToken, ValueTask<RetrievalResult>> documentLayer,
        Action<HierarchyProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(documentLayer);

        onProgress?.Invoke(new ProfileResolved(
            plan.Profile,
            [.. plan.Layers.Select(layer => layer.Name)],
            plan.Intent.Kind,
            plan.Intent.Explicit));

        var outcomes = new List<LayerOutcome>();
        var blocks = new List<LayerBlock>();
        RetrievalResult? documents = null;
        var producedAny = false;

        foreach (var layer in plan.Layers)
        {
            if (layer.Requirement is LayerRequirement.Fallback && producedAny)
            {
                continue;
            }

            onProgress?.Invoke(new LayerStarted(layer.Name, layer.Role, layer.Requirement));
            var started = Stopwatch.GetTimestamp();

            var matched = providers.FirstOrDefault(provider => layer.Sources.Any(provider.CanServe));
            var (block, retrieval) = matched is null
                ? await UnservedAsync(layer, documentLayer, onProgress, cancellationToken).ConfigureAwait(false)
                : await ServedAsync(matched, layer, plan.Intent, documentLayer, onProgress, cancellationToken)
                    .ConfigureAwait(false);

            documents ??= retrieval;

            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!block.IsEmpty())
            {
                producedAny = true;
            }

            var refusalCode = block is EvidenceRefusal refusal ? refusal.Code : null;
            onProgress?.Invoke(new LayerCompleted(
                layer.Name,
                block.KindName(),
                block.SupportsCompleteness(),
                refusalCode,
                elapsed));

            outcomes.Add(new LayerOutcome
            {
                Layer = layer.Name,
                Role = layer.Role,
                Requirement = layer.Requirement,
                Block = block.KindName(),
                EvidenceId = block.EvidenceId(),
                SupportsCompleteness = block.SupportsCompleteness(),
                RefusalCode = refusalCode,
                ElapsedMilliseconds = elapsed,
            });

            blocks.Add(new LayerBlock(layer.Name, block));
        }

        var completenessAvailable = blocks.Any(entry => entry.Block.SupportsCompleteness());
        var disclosedConflicts = CountConflicts(blocks);

        onProgress?.Invoke(new CoverageReported(completenessAvailable, disclosedConflicts));

        var decision = new EvidenceHierarchyDecision
        {
            Profile = plan.Profile,
            IntentKind = plan.Intent.Kind,
            IntentExplicit = plan.Intent.Explicit,
            Layers = outcomes,
            CompletenessAvailable = completenessAvailable,
            DisclosedConflicts = disclosedConflicts,
            ConflictsPolicy = plan.Conflicts,
        };

        return decision.RequiredLayerFailed() is { } failed
            ? new RequiredLayerUnavailable(failed.Layer, failed.RefusalCode ?? EvidenceRefusalCodes.Unavailable)
            : new HierarchyOutcome { Decision = decision, Blocks = blocks, Documents = documents };
    }

    /// <summary>
    /// Counts the cross-layer disagreements worth disclosing.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: a count in one layer against a count in another with a different value. Broad semantic
    /// conflict detection across a table and a passage is a model judgement rather than a deterministic one, and
    /// guessing at it would manufacture disclosures nobody can check.
    /// </remarks>
    /// <param name="blocks">The blocks the run produced.</param>
    /// <returns>How many disagreeing pairs the run found.</returns>
    public static int CountConflicts(IReadOnlyList<LayerBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var counts = new List<long>();
        foreach (var entry in blocks)
        {
            if (entry.Block is CountBlock count)
            {
                counts.Add(count.Value);
            }
        }

        var conflicts = 0;
        for (var index = 0; index < counts.Count; index++)
        {
            for (var other = index + 1; other < counts.Count; other++)
            {
                if (counts[index] != counts[other])
                {
                    conflicts++;
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Serves a layer no provider claimed: a document collection, or a refusal if the source names a plane.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="documentLayer">Runs the document path.</param>
    /// <param name="onProgress">The listener, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The block, and the retrieval when the document path ran.</returns>
    private static async ValueTask<(EvidenceBlock Block, RetrievalResult? Documents)> UnservedAsync(
        EvidenceLayer layer,
        Func<EvidenceLayer, CancellationToken, ValueTask<RetrievalResult>> documentLayer,
        Action<HierarchyProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        // A bare name is a collection, so the document path serves it. A plane-qualified name is not: refusing is
        // what stops a required layer reporting itself satisfied by a search that never consulted the plane.
        // Naming the source is right here - the runbook author pinned it themselves, and the hidden-source rule
        // protects sources a caller cannot see, not ones written into their own profile.
        return layer.Sources.FirstOrDefault(EvidencePlanes.IsPlaneQualified) is { } unbound
            ? Unbound(unbound)
            : await DocumentsAsync(layer, documentLayer, EvidencePlanes.Documents, onProgress, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Builds the refusal a plane-qualified source earns when nothing is bound to it.</summary>
    /// <param name="source">The unclaimed source.</param>
    /// <returns>The refusal, and no retrieval.</returns>
    private static (EvidenceBlock Block, RetrievalResult? Documents) Unbound(string source) => (
        new EvidenceRefusal
        {
            Code = EvidenceRefusalCodes.SourceNotBound,
            Message = "no provider is configured for this layer's sources",
            Source = source,
        },
        null);

    /// <summary>Serves a layer through the provider that claimed it.</summary>
    /// <param name="provider">The provider that claimed the layer.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="intent">What the turn intends to find out.</param>
    /// <param name="documentLayer">Runs the document path, when the claiming provider is the document path.</param>
    /// <param name="onProgress">The listener, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The block, and the retrieval when the document path ran.</returns>
    private static async ValueTask<(EvidenceBlock Block, RetrievalResult? Documents)> ServedAsync(
        IEvidenceProvider provider,
        EvidenceLayer layer,
        QueryIntent intent,
        Func<EvidenceLayer, CancellationToken, ValueTask<RetrievalResult>> documentLayer,
        Action<HierarchyProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        if (string.Equals(provider.Id, EvidencePlanes.Documents, StringComparison.Ordinal))
        {
            return await DocumentsAsync(layer, documentLayer, provider.Id, onProgress, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var source in layer.Sources.Where(provider.CanServe))
        {
            onProgress?.Invoke(new SourceBound(layer.Name, source, provider.Id));
        }

        return (await provider.FetchAsync(layer, intent, cancellationToken).ConfigureAwait(false), null);
    }

    /// <summary>Runs the document path for a layer.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="documentLayer">Runs the document path.</param>
    /// <param name="providerId">The id to report as the source's provider.</param>
    /// <param name="onProgress">The listener, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document hits block, and the full retrieval.</returns>
    private static async ValueTask<(EvidenceBlock Block, RetrievalResult? Documents)> DocumentsAsync(
        EvidenceLayer layer,
        Func<EvidenceLayer, CancellationToken, ValueTask<RetrievalResult>> documentLayer,
        string providerId,
        Action<HierarchyProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        foreach (var source in layer.Sources)
        {
            onProgress?.Invoke(new SourceBound(layer.Name, source, providerId));
        }

        var retrieval = await documentLayer(layer, cancellationToken).ConfigureAwait(false);

        return (new DocumentHits(retrieval.Chunks), retrieval);
    }
}

