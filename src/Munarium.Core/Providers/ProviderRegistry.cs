namespace Munarium.Providers;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Munarium.Budgets;

/// <summary>
/// The provider plane: the declarations a deployment applied, and what a live probe of them observes.
/// </summary>
/// <remarks>
/// The registry is a place declarations are kept, and a declaration is a dialect, an endpoint and the models that
/// dialect serves. It holds no credential and reads none into itself: a reference names where a key lives, the resolver
/// answers whether it is there, and the call that spends it is an adapter's - which the kernel never is.
/// <para>
/// Beside the applied declarations sit three synthesized, environment-backed defaults, one per reachable family. They
/// are listable and probed, and they are never stored, because they are the deployment's environment rather than
/// anything a caller wrote.
/// </para>
/// </remarks>
/// <param name="declarations">Where applied declarations are kept.</param>
/// <param name="credentials">Reads a credential from where a declaration says it lives.</param>
/// <param name="adapters">The adapters this deployment holds, or <see langword="null"/> when it holds none.</param>
/// <param name="ceiling">The deployment's paid-call ceilings, or <see langword="null"/> for the built-ins.</param>
/// <param name="now">Reads the current instant, so a test can move a rate window rather than wait for it.</param>
public sealed class ProviderRegistry(
    IProviderDeclarations declarations,
    ProviderCredentials credentials,
    IProviderAdapters? adapters = null,
    MaxTokensCeiling? ceiling = null,
    Func<DateTimeOffset>? now = null)
{
    /// <summary>The config name reserved for the default-provider rule.</summary>
    public const string DefaultSelector = "default";

    /// <summary>The source a declaration this deployment applied is listed under.</summary>
    public const string AppliedSource = "applied";

    /// <summary>The source a synthesized, environment-backed default is listed under.</summary>
    public const string DefaultSource = "default";

    private const string ProbePrompt = "Reply with the single word OK.";
    private const int RefusalDetailLimit = 300;

    private readonly IProviderDeclarations _declarations =
        declarations ?? throw new ArgumentNullException(nameof(declarations));
    private readonly ProviderCredentials _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly IProviderAdapters _adapters = adapters ?? ProviderAdapters.None;
    private readonly MaxTokensCeiling? _ceiling = ceiling;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _budgetGate = new();
    private readonly Dictionary<string, RateBudget> _budgets = [];

    /// <summary>Records a declaration.</summary>
    /// <param name="tenant">The tenant the declaration belongs to.</param>
    /// <param name="declaration">The declaration that was read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The declaration now in force, or why it was refused.</returns>
    public async ValueTask<ProviderDeclarationOutcome> ApplyAsync(
        string tenant,
        ProviderDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var reason = ProviderDeclaration.Refusal(declaration);

        if (reason is not null)
        {
            return new ProviderConfigRefused(reason);
        }

        var applied = await _declarations.SaveAsync(tenant, declaration, cancellationToken).ConfigureAwait(false);

        // A changed configuration starts a fresh window: keeping the old one would hold a new ceiling to what a previous
        // one had already spent.
        DropBudget(applied.Name);

        return applied;
    }

    /// <summary>
    /// Lists the plane: what this deployment applied, and the environment-backed defaults behind it.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One summary per configuration, applied first.</returns>
    public async ValueTask<IReadOnlyList<ProviderSummary>> ListAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        var applied = await _declarations.ListAsync(tenant, cancellationToken).ConfigureAwait(false);
        var providers = new List<ProviderSummary>(applied.Count + ProviderFamilies.DefaultPriority.Count);

        providers.AddRange(applied.Select(declaration => Summarize(declaration, AppliedSource)));

        foreach (var family in ProviderFamilies.DefaultPriority)
        {
            providers.Add(Summarize(DefaultDeclaration(family), DefaultSource));
        }

        return providers;
    }

    /// <summary>Reads one configuration's declaration, applied or synthesized.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="name">The name it was applied under, or <c>default-&lt;family&gt;</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The declaration, or <see langword="null"/> when this deployment holds none by that name.</returns>
    public async ValueTask<ProviderDeclaration?> DeclarationAsync(
        string tenant,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (await _declarations.FindAsync(tenant, name, cancellationToken).ConfigureAwait(false) is { } applied)
        {
            return applied;
        }

        var declared = ProviderFamilies.DefaultPriority.FirstOrDefault(family =>
            string.Equals(ProviderFamilies.DefaultName(family), name, StringComparison.Ordinal));

        return declared is null ? null : DefaultDeclaration(declared);
    }

    /// <summary>Probes one configuration through the adapter this deployment holds for its family.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="name">The configuration's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was observed, or that no such configuration is held.</returns>
    public async ValueTask<ProviderProbeOutcome> HealthAsync(
        string tenant,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (await DeclarationAsync(tenant, name, cancellationToken).ConfigureAwait(false) is not { } declaration)
        {
            return new UnknownProviderConfig(name);
        }

        var fingerprint = Fingerprint(declaration);
        var credential = _credentials.Resolve(declaration.Credential);

        if (ProviderFamilies.NeedsCredential(declaration.Family) && !credential.Resolved)
        {
            return new ProviderProbe(false, declaration.Family, fingerprint, credential.Detail);
        }

        if (_adapters.AdapterFor(declaration.Family) is not { } adapter)
        {
            return new ProviderProbe(
                Healthy: false,
                declaration.Family,
                fingerprint,
                $"this deployment holds no '{declaration.Family}' adapter, so nothing here can be probed");
        }

        try
        {
            var health = await adapter.HealthAsync(cancellationToken).ConfigureAwait(false);

            return new ProviderProbe(
                health.Healthy,
                declaration.Family,
                health.EndpointFingerprint is { Length: > 0 } reported ? reported : fingerprint,
                health.Detail);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or TimeoutException
            or InvalidOperationException or TaskCanceledException)
        {
            return new ProviderProbe(false, declaration.Family, fingerprint, Detail(failure));
        }
    }

    /// <summary>
    /// Probes the built-in tier models: one small completion per reachable family and tier.
    /// </summary>
    /// <remarks>
    /// The environment-backed defaults only, never an applied configuration and never a budget - which is what makes
    /// this answerable by an operator who has configured nothing yet, and what makes a family whose conventional
    /// variable is unset a <em>skipped</em> check rather than a failure. It spends real tokens when it runs, which is
    /// why it is a route of its own rather than part of liveness.
    /// </remarks>
    /// <param name="tenant">The tenant whose ceiling the probes are given.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One check per family and tier, in the order they are probed.</returns>
    public async ValueTask<IReadOnlyList<ProviderProbeCheck>> ProbeAllAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<ProviderProbeCheck>(
            ProviderFamilies.DefaultPriority.Count * ModelTiers.All.Count);

        var maxTokens = await ProbeCeilingAsync(tenant, cancellationToken).ConfigureAwait(false);

        foreach (var family in ProviderFamilies.DefaultPriority)
        {
            var declaration = DefaultDeclaration(family);

            foreach (var tier in ModelTiers.All)
            {
                if (declaration.TierModel(tier) is { } model)
                {
                    checks.Add(await ProbeAsync(declaration, tier, model, maxTokens, cancellationToken)
                        .ConfigureAwait(false));
                }
            }
        }

        return checks;
    }

    /// <summary>
    /// Whether the plane is healthy: at least one check ran, and every check that ran passed.
    /// </summary>
    /// <remarks>
    /// The original's rule, ported: a skipped check is not a failure, so a deployment with no credential at all reads
    /// as not healthy rather than as vacuously healthy - there is nothing there to be well.
    /// </remarks>
    /// <param name="checks">The checks a probe produced.</param>
    /// <returns><see langword="true"/> when something was probed and everything probed passed.</returns>
    public static bool PlaneHealthy(IEnumerable<ProviderProbeCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        var probed = checks.Where(check => !check.Skipped).ToList();

        return probed.Count > 0 && probed.TrueForAll(check => check.Ok);
    }

    /// <summary>Probes one family and tier through the adapter this deployment holds.</summary>
    /// <param name="declaration">The declaration being probed.</param>
    /// <param name="tier">The tier.</param>
    /// <param name="model">The concrete model to probe.</param>
    /// <param name="maxTokens">The ceiling the probe is given.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The check.</returns>
    private async ValueTask<ProviderProbeCheck> ProbeAsync(
        ProviderDeclaration declaration,
        ModelTier tier,
        string model,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var tierName = ModelTiers.Name(tier);
        var credential = _credentials.Resolve(declaration.Credential);

        if (ProviderFamilies.NeedsCredential(declaration.Family) && !credential.Resolved)
        {
            return new ProviderProbeCheck(
                declaration.Family, tierName, model, Ok: false, Skipped: true, LatencyMs: null, credential.Detail);
        }

        if (_adapters.AdapterFor(declaration.Family) is not { } adapter)
        {
            return new ProviderProbeCheck(
                declaration.Family,
                tierName,
                model,
                Ok: false,
                Skipped: false,
                LatencyMs: null,
                $"this deployment holds no '{declaration.Family}' adapter");
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            var completion = await adapter
                .CompleteAsync(
                    new CompletionRequest
                    {
                        Model = model,
                        Prompt = ProbePrompt,
                        MaxTokens = maxTokens,
                        Temperature = 0,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            long latency = Stopwatch.GetElapsedTime(started).Milliseconds;

            return completion.Text.Trim().Length > 0
                ? new ProviderProbeCheck(
                    declaration.Family, tierName, model, Ok: true, Skipped: false, latency,
                    $"ok (stop reason '{completion.StopReason}')")
                : new ProviderProbeCheck(
                    declaration.Family, tierName, model, Ok: false, Skipped: false, latency,
                    $"empty completion (stop reason '{completion.StopReason}')");
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or TimeoutException
            or InvalidOperationException or TaskCanceledException)
        {
            long latency = Stopwatch.GetElapsedTime(started).Milliseconds;

            return new ProviderProbeCheck(
                declaration.Family, tierName, model, Ok: false, Skipped: false, latency, Detail(failure));
        }
    }

    /// <summary>
    /// Relays a completion: this deployment spends its own credential on the caller's behalf.
    /// </summary>
    /// <remarks>
    /// The order is the design. The configuration resolves first, so an unknown name costs nothing; the family's
    /// usability is established before the ceiling is read and long before an adapter is called; the model resolves in
    /// the original's order - an explicit model, then the tier, then the configuration's first completion model; and the
    /// declared budget is checked with an estimate and recorded with what the call cost. What comes back is not turn
    /// evidence and is recorded nowhere: a caller using a deployment's model is a different act from a conversation that
    /// reads a corpus.
    /// </remarks>
    /// <param name="tenant">The tenant that applied the configuration.</param>
    /// <param name="name">The configuration's name, or <see cref="DefaultSelector"/> for the default-provider rule.</param>
    /// <param name="query">What to ask for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completion, or why it was refused.</returns>
    public async ValueTask<ProviderCompletionOutcome> CompleteAsync(
        string tenant,
        string name,
        ProviderCompletionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.VersionId is { Length: > 0 })
        {
            return new ProviderCallRefused(NoInvocationProvenance, ProviderCallRefused.InvalidInput);
        }

        var resolved = await ResolveCallAsync(tenant, name, query.Provider, cancellationToken).ConfigureAwait(false);

        if (resolved.Refused is { } refused)
        {
            return refused;
        }

        var declaration = resolved.Declaration!;
        var tier = query.Tier ?? ModelTier.Capable;

        // The original's order, ported exactly: an explicit model, then the tier the caller named, then the
        // configuration's own first model, then the family's capable built-in. A caller who names no tier gets what the
        // configuration offers rather than what the family recommends.
        var model = query.Model
            ?? (query.Tier is { } asked ? declaration.TierModel(asked) : null)
            ?? declaration.Models.FirstComplete
            ?? ProviderFamilies.BuiltinTierModel(declaration.Family, ModelTier.Capable);

        if (string.IsNullOrWhiteSpace(model))
        {
            return new ProviderCallRefused("no model given or configured", ProviderCallRefused.InvalidInput);
        }

        if (_adapters.AdapterFor(declaration.Family) is not { } adapter)
        {
            return new ProviderCallRefused(NoAdapter(declaration.Family), ProviderCallRefused.Unavailable);
        }

        var defaultCeiling = (await CeilingAsync(tenant, cancellationToken).ConfigureAwait(false)).CompleteDefault;
        var budget = BudgetFor(declaration);

        if (budget.Check(tier, Estimate(query.System, query.Prompt)) is { } limited)
        {
            return new ProviderCallRefused(limited, ProviderCallRefused.RateLimited);
        }

        try
        {
            var completion = await adapter
                .CompleteAsync(
                    new CompletionRequest
                    {
                        Model = model,
                        System = query.System ?? string.Empty,
                        Prompt = query.Prompt,
                        MaxTokens = query.MaxTokens ?? defaultCeiling,
                        Temperature = query.Temperature ?? 0,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            budget.Record(tier, completion.Usage.InputTokens + completion.Usage.OutputTokens);

            return new ProviderCompletion(
                declaration.Family,
                completion.Text,
                completion.Model is { Length: > 0 } served ? served : model,
                completion.StopReason,
                completion.Usage.InputTokens,
                completion.Usage.OutputTokens);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or TimeoutException
            or InvalidOperationException or TaskCanceledException)
        {
            return new ProviderCallRefused(Detail(failure), ProviderCallRefused.Unavailable);
        }
    }

    /// <summary>Relays an embedding: one vector per text, from the deployment's own credential.</summary>
    /// <remarks>
    /// No ceiling applies here, because an embedding has no output budget: what is checked is the configuration's own
    /// budget, against the size of the texts. The port keeps no embedding cache, so <c>cache_hit</c> is reported as false
    /// rather than invented - a cache is a cost control, and this plane reports what it did.
    /// </remarks>
    /// <param name="tenant">The tenant that applied the configuration.</param>
    /// <param name="name">The configuration's name, or <see cref="DefaultSelector"/> for the default-provider rule.</param>
    /// <param name="query">What to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The vectors, or why they could not be produced.</returns>
    public async ValueTask<ProviderEmbeddingOutcome> EmbedAsync(
        string tenant,
        string name,
        ProviderEmbeddingQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.VersionId is { Length: > 0 })
        {
            return new ProviderCallRefused(NoInvocationProvenance, ProviderCallRefused.InvalidInput);
        }

        if (query.Inputs is not { Count: > 0 })
        {
            return new ProviderCallRefused("inputs is required", ProviderCallRefused.InvalidInput);
        }

        var resolved = await ResolveCallAsync(tenant, name, query.Provider, cancellationToken).ConfigureAwait(false);

        if (resolved.Refused is { } refused)
        {
            return refused;
        }

        var declaration = resolved.Declaration!;
        var model = query.Model ?? declaration.Models.FirstEmbed;

        if (string.IsNullOrWhiteSpace(model))
        {
            return new ProviderCallRefused("no embed model given or configured", ProviderCallRefused.InvalidInput);
        }

        if (_adapters.AdapterFor(declaration.Family) is not { } adapter)
        {
            return new ProviderCallRefused(NoAdapter(declaration.Family), ProviderCallRefused.Unavailable);
        }

        var budget = BudgetFor(declaration);

        if (budget.Check(ModelTier.Capable, Estimate(query.Inputs)) is { } limited)
        {
            return new ProviderCallRefused(limited, ProviderCallRefused.RateLimited);
        }

        try
        {
            var embedded = await adapter
                .EmbedAsync(new EmbeddingRequest { Model = model, Inputs = query.Inputs }, cancellationToken)
                .ConfigureAwait(false);

            budget.Record(ModelTier.Capable, embedded.Usage.InputTokens + embedded.Usage.OutputTokens);

            return new ProviderEmbedding(
                declaration.Family,
                [.. embedded.Vectors.Select(vector => vector.ToArray())],
                embedded.Model is { Length: > 0 } served ? served : model,
                embedded.Vectors.Count == 0 ? 0 : embedded.Vectors[0].Length,
                CacheHit: false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or TimeoutException
            or InvalidOperationException or TaskCanceledException)
        {
            return new ProviderCallRefused(Detail(failure), ProviderCallRefused.Unavailable);
        }
    }

    /// <summary>Resolves the configuration a relayed call is made through.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="name">The configuration's name, or the reserved default selector.</param>
    /// <param name="family">The family override, only honoured on the reserved name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The configuration to call, or why nothing can be called.</returns>
    private async ValueTask<CallTarget> ResolveCallAsync(
        string tenant,
        string name,
        string? family,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(name, DefaultSelector, StringComparison.Ordinal))
        {
            if (family is not null)
            {
                return new CallTarget(null, new ProviderCallRefused(
                    $"the provider field requires the reserved '{DefaultSelector}' config name",
                    ProviderCallRefused.InvalidInput));
            }

            var named = await DeclarationAsync(tenant, name, cancellationToken).ConfigureAwait(false);

            return named is null
                ? new CallTarget(null, new ProviderCallRefused(
                    $"No provider config named '{name}' is held by this deployment.",
                    ProviderCallRefused.UnknownConfiguration))
                : Target(named);
        }

        if (family is not null && !ProviderFamilies.IsKnown(family))
        {
            return new CallTarget(null, new ProviderCallRefused(
                $"unsupported provider '{family}' (anthropic|openai|openrouter|ollama)",
                ProviderCallRefused.InvalidInput));
        }

        IReadOnlyList<string> families = family is null ? ProviderFamilies.DefaultPriority : [family];
        var applied = await _declarations.ListAsync(tenant, cancellationToken).ConfigureAwait(false);

        foreach (var candidate in families)
        {
            // A configuration somebody applied wins over the synthesized default, and one per family is enough: the
            // default rule asks which family can answer, not which of several configurations should.
            var chosen = applied
                .Where(declaration => string.Equals(declaration.Family, candidate, StringComparison.Ordinal))
                .OrderBy(declaration => declaration.Name, StringComparer.Ordinal)
                .FirstOrDefault()
                ?? DefaultDeclaration(candidate);

            if (UnusableReason(chosen) is null)
            {
                return new CallTarget(chosen, null);
            }
        }

        return new CallTarget(null, new ProviderCallRefused(
            NoUsableFamily(family),
            ProviderCallRefused.Unavailable));
    }

    /// <summary>What a named configuration is worth calling, or why it cannot be called.</summary>
    /// <param name="declaration">The configuration that was found.</param>
    /// <returns>The target, or the refusal that stands in its place.</returns>
    private CallTarget Target(ProviderDeclaration declaration) =>
        UnusableReason(declaration) is { } unusable
            ? new CallTarget(null, new ProviderCallRefused(unusable, ProviderCallRefused.Unavailable))
            : new CallTarget(declaration, null);

    /// <summary>Whether a configuration can actually be called, and why not when it cannot.</summary>
    /// <param name="declaration">The configuration.</param>
    /// <returns>The reason, or <see langword="null"/> when this deployment can call it.</returns>
    private string? UnusableReason(ProviderDeclaration declaration)
    {
        if (ProviderFamilies.NeedsCredential(declaration.Family))
        {
            var credential = _credentials.Resolve(declaration.Credential);

            if (!credential.Resolved)
            {
                return credential.Detail;
            }
        }

        return _adapters.AdapterFor(declaration.Family) is null ? NoAdapter(declaration.Family) : null;
    }

    /// <summary>What to say when this deployment holds no adapter for a family.</summary>
    /// <param name="family">The family.</param>
    /// <returns>The reason.</returns>
    private static string NoAdapter(string family) =>
        $"this deployment holds no '{family}' adapter, so it cannot call that family";

    /// <summary>What to say when no family at all can be called.</summary>
    /// <param name="family">The family that was asked for, or <see langword="null"/> for the default rule.</param>
    /// <returns>The reason, naming what was checked.</returns>
    private static string NoUsableFamily(string? family) => family is null
        ? "no default provider is usable here (checked MUNARIUM_SECRET_ANTHROPIC, MUNARIUM_SECRET_OPENAI, "
            + "MUNARIUM_SECRET_OPENROUTER, the applied configs, and the adapters this deployment holds)"
        : $"no usable '{family}' configuration here (checked the applied configs, the conventional variable for that "
            + "family, and the adapters this deployment holds)";

    /// <summary>The window a configuration's declared budget is enforced over, made once and kept.</summary>
    /// <param name="declaration">The configuration.</param>
    /// <returns>The window.</returns>
    private RateBudget BudgetFor(ProviderDeclaration declaration)
    {
        lock (_budgetGate)
        {
            if (_budgets.TryGetValue(declaration.Name, out var held))
            {
                return held;
            }

            var created = new RateBudget(declaration.Budgets, _now);

            _budgets[declaration.Name] = created;

            return created;
        }
    }

    /// <summary>Forgets a configuration's window, so a changed ceiling starts where it says it does.</summary>
    /// <param name="name">The configuration's name.</param>
    private void DropBudget(string name)
    {
        lock (_budgetGate)
        {
            _budgets.Remove(name);
        }
    }

    /// <summary>Reads the ceilings that apply to one tenant.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ceilings, or the built-ins when this registry was composed without a ceiling.</returns>
    private async ValueTask<MaxTokensBudget> CeilingAsync(string tenant, CancellationToken cancellationToken) =>
        _ceiling is null
            ? MaxTokensBudget.Builtin
            : (await _ceiling.EffectiveAsync(tenant, cancellationToken).ConfigureAwait(false)).Budgets;

    /// <summary>Reads the ceiling one probe completion is given.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The probe ceiling.</returns>
    private async ValueTask<int> ProbeCeilingAsync(string tenant, CancellationToken cancellationToken) =>
        (await CeilingAsync(tenant, cancellationToken).ConfigureAwait(false)).HealthAiProbe;

    /// <summary>Estimates what a completion will cost, the way a rate guard has to: before it is paid for.</summary>
    /// <param name="system">The system instruction, when there is one.</param>
    /// <param name="prompt">The prompt.</param>
    /// <returns>The estimate, in tokens.</returns>
    private static long Estimate(string? system, string prompt) =>
        ((system?.Length ?? 0) + prompt.Length) / 4 + 1;

    /// <summary>Estimates what embedding these texts will cost.</summary>
    /// <param name="inputs">The texts.</param>
    /// <returns>The estimate, in tokens.</returns>
    private static long Estimate(IReadOnlyList<string> inputs) =>
        inputs.Sum(input => input.Length / 4) + 1;

    /// <summary>What to say when a caller asks for an invocation to be recorded and this port cannot record one.</summary>
    private const string NoInvocationProvenance =
        "version_id names an invocation to record, and the invocation-provenance plane is not ported: there is no "
        + "POST /v1/versions/{version_id}/events here, so the event could not be written and the call is refused rather "
        + "than made unrecorded";

    /// <summary>The configuration a relayed call was resolved to, or why none could be.</summary>
    /// <param name="Declaration">The configuration to call.</param>
    /// <param name="Refused">Why nothing could be called.</param>
    private readonly record struct CallTarget(ProviderDeclaration? Declaration, ProviderCallRefused? Refused);

    /// <summary>Reads a configuration the way an operator reads it: what it resolves to, and whether its key is there.</summary>
    /// <param name="declaration">The declaration.</param>
    /// <param name="source">Where the configuration came from.</param>
    /// <returns>The summary.</returns>
    private ProviderSummary Summarize(ProviderDeclaration declaration, string source) => new(
        declaration.Name,
        declaration.Family,
        source,
        !ProviderFamilies.NeedsCredential(declaration.Family) || _credentials.Resolve(declaration.Credential).Resolved,
        declaration.TierModel(ModelTier.Fast),
        declaration.TierModel(ModelTier.Capable),
        declaration.TierModel(ModelTier.Frontier));

    /// <summary>The declaration a family's conventional environment variable stands for.</summary>
    /// <param name="family">The family.</param>
    /// <returns>The declaration, which is never stored.</returns>
    private static ProviderDeclaration DefaultDeclaration(string family) => new()
    {
        Name = ProviderFamilies.DefaultName(family),
        Provider = new ProviderId(family),
        Credential = ProviderFamilies.DefaultEnvironmentVariable(family) is { } variable
            ? CredentialReference.ForEnvironment(variable)
            : null,
    };

    /// <summary>Fingerprints the endpoint a declaration names - which is not, and never includes, a credential.</summary>
    /// <param name="declaration">The declaration.</param>
    /// <returns>The fingerprint.</returns>
    private static string Fingerprint(ProviderDeclaration declaration) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{declaration.Family}|{declaration.Endpoint}")))[..16];

    /// <summary>Reads a failure into something a probe can report without carrying material or a whole body.</summary>
    /// <param name="failure">The failure.</param>
    /// <returns>The detail, bounded the way the original bounds it.</returns>
    private static string Detail(Exception failure) =>
        failure.Message.Length > RefusalDetailLimit ? failure.Message[..RefusalDetailLimit] : failure.Message;
}

/// <summary>No configuration of that name is held by this deployment.</summary>
/// <param name="Name">The name that was asked for.</param>
public sealed record UnknownProviderConfig(string Name);

/// <summary>
/// What probing one configuration produced: what was observed, or that nothing is held under that name.
/// </summary>
public readonly union ProviderProbeOutcome(ProviderProbe, UnknownProviderConfig);
