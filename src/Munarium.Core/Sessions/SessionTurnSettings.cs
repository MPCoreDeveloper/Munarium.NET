namespace Munarium.Sessions;

using Munarium.Budgets;
using Munarium.Evidence;
using Munarium.Providers;
using Munarium.Retrieval;

/// <summary>
/// The deployment's serving configuration: which models answer, over which corpus, and for whom.
/// </summary>
/// <param name="Embedder">The model that embeds the question.</param>
/// <param name="Model">The model that classifies and answers.</param>
/// <param name="EmbeddingModel">The embedding model to ask for.</param>
/// <param name="Providers">The evidence providers, in trust order.</param>
/// <param name="Tenant">The deployment's tenant.</param>
/// <param name="Collections">
/// The collections this deployment can search one at a time, or <see langword="null"/> when it built one corpus.
/// </param>
/// <param name="Ceiling">
/// The deployment's paid-call ceilings, or <see langword="null"/> for the built-ins. A turn reads its completion, its
/// expansion and its classifier ceilings here, because a runbook that declares none has to be held to what the
/// deployment says rather than to a constant compiled into the kernel.
/// </param>
/// <remarks>
/// Named rather than passed one at a time, because these are one decision - which model answers this deployment's
/// questions, over what it can read, on whose behalf - and a turn that disagreed with itself about any of them would be
/// answering a question nobody asked.
/// </remarks>
public sealed record SessionTurnSettings(
    IModelProvider Embedder,
    IModelProvider Model,
    string EmbeddingModel,
    IReadOnlyList<IEvidenceProvider> Providers,
    string Tenant,
    ICollectionIndexes? Collections = null,
    MaxTokensCeiling? Ceiling = null);
