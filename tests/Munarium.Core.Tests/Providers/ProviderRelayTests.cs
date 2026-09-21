namespace Munarium.Core.Tests.Providers;

using Munarium.Budgets;
using Munarium.Core.Tests.Support;
using Munarium.Providers;

/// <summary>
/// The relay: what a deployment spends its own credential on, and in what order it decides.
/// </summary>
/// <remarks>
/// The clock is injected, because a rate window is a rule about time and a test that waited a minute to prove one would
/// be a test nobody runs. The credential resolver is injected for the same reason a probe's is: a test that set a
/// process-wide variable would be deciding it for every test running beside it.
/// </remarks>
public class ProviderRelayTests
{
    private const string Tenant = "acme";

    [Fact]
    public async Task AModelResolvesAsAnExplicitModelThenTheTierThenTheConfigurationsOwn()
    {
        var adapter = new ProbeProvider(ProviderFamilies.Anthropic);
        var registry = Relay([adapter]);
        await registry.ApplyAsync(Tenant, Declaration(models: new ProviderModels { Complete = ["office-model"] }));

        var named = await Complete(registry, new ProviderCompletionQuery { Prompt = "hi", Model = "named" });
        var tier = await Complete(registry, new ProviderCompletionQuery { Prompt = "hi", Tier = ModelTier.Fast });
        var configured = await Complete(registry, new ProviderCompletionQuery { Prompt = "hi" });

        Assert.Equal("named", named.Model);
        Assert.Equal("claude-haiku-4-5", tier.Model);
        Assert.Equal("office-model", configured.Model);
        Assert.Equal(
            ["named", "claude-haiku-4-5", "office-model"],
            adapter.Completions.Select(request => request.Model));
    }

    [Fact]
    public async Task AConfigurationThatNamesNoModelFallsBackToTheFamilysCapableBuiltIn()
    {
        var registry = Relay();
        await registry.ApplyAsync(Tenant, Declaration(models: new ProviderModels()));

        var completion = await Complete(registry, new ProviderCompletionQuery { Prompt = "hi" });

        Assert.Equal("claude-sonnet-5", completion.Model);
    }

    [Fact]
    public async Task TheCeilingAppliesToTheRequestAndToTheAdapterCall()
    {
        var adapter = new ProbeProvider(ProviderFamilies.Anthropic);
        var registry = Relay(
            [adapter],
            ceiling: new MaxTokensCeiling(
                new InMemoryMaxTokensStore(),
                MaxTokensBudget.Builtin with { CompleteDefault = 128 }));

        await registry.ApplyAsync(Tenant, Declaration());

        await Complete(registry, new ProviderCompletionQuery { Prompt = "hi" });
        await Complete(registry, new ProviderCompletionQuery { Prompt = "hi", MaxTokens = 4096 });

        Assert.Equal(128, adapter.Completions[0].MaxTokens);
        Assert.Equal(4096, adapter.Completions[1].MaxTokens);
    }

    [Fact]
    public async Task ANameNothingIsHeldUnderCostsNothing()
    {
        var adapter = new ProbeProvider(ProviderFamilies.Anthropic);
        var registry = Relay([adapter]);

        var refused = await Refused(registry, "nobody", new ProviderCompletionQuery { Prompt = "hi" });

        Assert.Equal(ProviderCallRefused.UnknownConfiguration, refused.Status);
        Assert.Equal("No provider config named 'nobody' is held by this deployment.", refused.Reason);
        Assert.Empty(adapter.Completions);
    }

    [Fact]
    public async Task AFamilyOverrideBelongsToTheReservedName()
    {
        var registry = Relay();
        await registry.ApplyAsync(Tenant, Declaration());

        var refused = await Refused(
            registry,
            "office",
            new ProviderCompletionQuery { Prompt = "hi", Provider = ProviderFamilies.OpenAi });

        Assert.Equal(ProviderCallRefused.InvalidInput, refused.Status);
        Assert.Equal("the provider field requires the reserved 'default' config name", refused.Reason);
    }

    [Fact]
    public async Task AskingForAnInvocationToBeRecordedIsRefusedRatherThanIgnored()
    {
        var adapter = new ProbeProvider(ProviderFamilies.Anthropic);
        var registry = Relay([adapter]);
        await registry.ApplyAsync(Tenant, Declaration());

        var refused = await Refused(
            registry,
            "office",
            new ProviderCompletionQuery { Prompt = "hi", VersionId = "memory@1" });

        Assert.Equal(ProviderCallRefused.InvalidInput, refused.Status);
        Assert.Contains("invocation-provenance plane is not ported", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(adapter.Completions);
    }

    [Fact]
    public async Task AKeyThatDoesNotResolveAndAnAbsentAdapterAreBothNamed()
    {
        var noKey = Relay(families: []);
        await noKey.ApplyAsync(Tenant, Declaration());

        var keyRefused = await Refused(noKey, "office", new ProviderCompletionQuery { Prompt = "hi" });

        Assert.Equal(ProviderCallRefused.Unavailable, keyRefused.Status);
        Assert.Contains("'MUNARIUM_SECRET_ANTHROPIC' is not set", keyRefused.Reason, StringComparison.Ordinal);

        var noAdapter = Relay(adapters: []);
        await noAdapter.ApplyAsync(Tenant, Declaration());

        var adapterRefused = await Refused(noAdapter, "office", new ProviderCompletionQuery { Prompt = "hi" });

        Assert.Equal(ProviderCallRefused.Unavailable, adapterRefused.Status);
        Assert.Contains("holds no 'anthropic' adapter", adapterRefused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultRuleTakesTheFirstFamilyThatIsUsable()
    {
        var both = Relay(
            [new ProbeProvider(ProviderFamilies.Anthropic), new ProbeProvider(ProviderFamilies.OpenAi)],
            families: [ProviderFamilies.Anthropic, ProviderFamilies.OpenAi]);

        var first = await Complete(
            both,
            new ProviderCompletionQuery { Prompt = "hi" },
            name: ProviderRegistry.DefaultSelector);

        Assert.Equal(ProviderFamilies.Anthropic, first.Provider);

        var named = await Complete(
            both,
            new ProviderCompletionQuery { Prompt = "hi", Provider = ProviderFamilies.OpenAi },
            name: ProviderRegistry.DefaultSelector);

        Assert.Equal(ProviderFamilies.OpenAi, named.Provider);

        // Anthropic is passed over when only its neighbour has a key, which is what "the first usable family" means.
        var openaiOnly = Relay(
            [new ProbeProvider(ProviderFamilies.Anthropic), new ProbeProvider(ProviderFamilies.OpenAi)],
            families: [ProviderFamilies.OpenAi]);

        var fallback = await Complete(
            openaiOnly,
            new ProviderCompletionQuery { Prompt = "hi" },
            name: ProviderRegistry.DefaultSelector);

        Assert.Equal(ProviderFamilies.OpenAi, fallback.Provider);
    }

    [Fact]
    public async Task TheDefaultRuleRefusesByNameWhenNothingIsUsable()
    {
        var registry = Relay(adapters: [], families: []);

        var refused = await Refused(
            registry,
            ProviderRegistry.DefaultSelector,
            new ProviderCompletionQuery { Prompt = "hi" });

        Assert.Equal(ProviderCallRefused.Unavailable, refused.Status);
        Assert.Contains("no default provider is usable here", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("MUNARIUM_SECRET_ANTHROPIC", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallTheProviderFailsIsAFailedCallRatherThanARefusalOfTheRequest()
    {
        var registry = Relay(
            [new ProbeProvider(ProviderFamilies.Anthropic, failure: new HttpRequestException("the provider answered 529"))]);
        await registry.ApplyAsync(Tenant, Declaration());

        var refused = await Refused(registry, "office", new ProviderCompletionQuery { Prompt = "hi" });

        Assert.Equal(ProviderCallRefused.Unavailable, refused.Status);
        Assert.Equal("the provider answered 529", refused.Reason);
    }

    [Fact]
    public async Task AnEmbeddingAnswersOneVectorPerInputAndReportsItsOwnDimensions()
    {
        var adapter = new ProbeProvider(ProviderFamilies.Anthropic, dimensions: 8);
        var registry = Relay([adapter]);
        await registry.ApplyAsync(Tenant, Declaration(models: new ProviderModels { Embed = ["office-embed"] }));

        var embedding = await Embedded(registry, new ProviderEmbeddingQuery { Inputs = ["first", "second"] });

        Assert.Equal(2, embedding.Vectors.Count);
        Assert.Equal(8, embedding.Dimensions);
        Assert.Equal("office-embed", embedding.Model);
        Assert.Equal(ProviderFamilies.Anthropic, embedding.Provider);

        // No cache is kept, so the plane says so rather than reporting a hit it did not have.
        Assert.False(embedding.CacheHit);
    }

    [Fact]
    public async Task AnEmbeddingWithoutInputsOrWithoutAModelIsRefusedRatherThanGuessed()
    {
        var registry = Relay();
        await registry.ApplyAsync(Tenant, Declaration(models: new ProviderModels()));

        var noInputs = Refused(await registry.EmbedAsync(Tenant, "office", new ProviderEmbeddingQuery { Inputs = [] }));
        var noModel = Refused(await registry.EmbedAsync(
            Tenant,
            "office",
            new ProviderEmbeddingQuery { Inputs = ["first"] }));

        Assert.Equal("inputs is required", noInputs.Reason);
        Assert.Equal("no embed model given or configured", noModel.Reason);
    }

    [Fact]
    public async Task ADeclaredRequestCeilingRefusesTheNextCallInTheSameMinute()
    {
        var clock = new Clock();
        var registry = Relay(now: clock.Now);
        await registry.ApplyAsync(Tenant, Declaration(budgets: new ProviderBudgets { RequestsPerMinute = 1 }));

        await Complete(registry, new ProviderCompletionQuery { Prompt = "hi" });

        var refused = await Refused(registry, "office", new ProviderCompletionQuery { Prompt = "hi" });

        Assert.Equal(ProviderCallRefused.RateLimited, refused.Status);
        Assert.Equal("rpm budget 1 is exhausted for this minute", refused.Reason);

        // The window rolls, so what is declared is a rate rather than a lifetime cap.
        clock.Advance(TimeSpan.FromSeconds(61));

        await Complete(registry, new ProviderCompletionQuery { Prompt = "hi" });
    }

    [Fact]
    public async Task ADailyTokenCeilingIsReachedOnWhatTheCallsActuallyCost()
    {
        var registry = Relay();
        await registry.ApplyAsync(Tenant, Declaration(budgets: new ProviderBudgets { Fast = 1 }));

        await Complete(registry, new ProviderCompletionQuery { Prompt = "hi", Tier = ModelTier.Fast });

        var refused = await Refused(
            registry,
            "office",
            new ProviderCompletionQuery { Prompt = "hi", Tier = ModelTier.Fast });

        Assert.Equal(ProviderCallRefused.RateLimited, refused.Status);
        Assert.Equal("the daily token ceiling 1 is reached for the fast tier", refused.Reason);

        // Another tier carries its own ceiling, which is what a per-tier cap means.
        await Complete(registry, new ProviderCompletionQuery { Prompt = "hi", Tier = ModelTier.Capable });
    }

    [Fact]
    public async Task ChangingAConfigurationsCeilingStartsItsWindowAgain()
    {
        var adapter = new ProbeProvider(ProviderFamilies.Anthropic);
        var registry = Relay([adapter]);
        await registry.ApplyAsync(Tenant, Declaration(budgets: new ProviderBudgets { RequestsPerMinute = 1 }));

        await Complete(registry, new ProviderCompletionQuery { Prompt = "hi" });

        await registry.ApplyAsync(Tenant, Declaration(budgets: new ProviderBudgets { RequestsPerMinute = 5 }));

        await Complete(registry, new ProviderCompletionQuery { Prompt = "hi" });
        await Complete(registry, new ProviderCompletionQuery { Prompt = "hi" });

        Assert.Equal(3, adapter.Completions.Count);
    }

    /// <summary>A completion through the registry, insisting that it was answered.</summary>
    /// <param name="registry">The registry.</param>
    /// <param name="query">What to ask for.</param>
    /// <param name="name">The configuration's name.</param>
    /// <returns>The completion.</returns>
    private static async Task<ProviderCompletion> Complete(
        ProviderRegistry registry,
        ProviderCompletionQuery query,
        string name = "office")
    {
        var outcome = await registry.CompleteAsync(Tenant, name, query);

        return outcome switch
        {
            ProviderCompletion completion => completion,
            ProviderCallRefused refused => throw new InvalidOperationException(
                $"refused with {refused.Status}: {refused.Reason}"),
        };
    }

    /// <summary>An embedding through the registry, insisting that it was answered.</summary>
    /// <param name="registry">The registry.</param>
    /// <param name="query">What to embed.</param>
    /// <returns>The embedding.</returns>
    private static async Task<ProviderEmbedding> Embedded(ProviderRegistry registry, ProviderEmbeddingQuery query)
    {
        var outcome = await registry.EmbedAsync(Tenant, "office", query);

        return outcome switch
        {
            ProviderEmbedding embedding => embedding,
            ProviderCallRefused refused => throw new InvalidOperationException(
                $"refused with {refused.Status}: {refused.Reason}"),
        };
    }

    /// <summary>A call, insisting that it was refused.</summary>
    /// <param name="registry">The registry.</param>
    /// <param name="name">The configuration's name.</param>
    /// <param name="query">What to ask for.</param>
    /// <returns>The refusal.</returns>
    private static async Task<ProviderCallRefused> Refused(
        ProviderRegistry registry,
        string name,
        ProviderCompletionQuery query)
    {
        var outcome = await registry.CompleteAsync(Tenant, name, query);

        return outcome switch
        {
            ProviderCallRefused refused => refused,
            ProviderCompletion completion => throw new InvalidOperationException(
                $"answered '{completion.Text}' rather than refusing"),
        };
    }

    /// <summary>Reads an embedding outcome, insisting that it was refused.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The refusal.</returns>
    private static ProviderCallRefused Refused(ProviderEmbeddingOutcome outcome) => outcome switch
    {
        ProviderCallRefused refused => refused,
        ProviderEmbedding embedding => throw new InvalidOperationException(
            $"embedded {embedding.Vectors.Count} inputs rather than refusing"),
    };

    /// <summary>A registry over in-memory declarations, with an Anthropic adapter unless told otherwise.</summary>
    /// <param name="adapters">The adapters to compose, or <see langword="null"/> for an Anthropic one.</param>
    /// <param name="families">The families a credential resolves for.</param>
    /// <param name="ceiling">The paid-call ceilings, or <see langword="null"/> for the built-ins.</param>
    /// <param name="now">Reads the current instant, or <see langword="null"/> for the wall clock.</param>
    /// <returns>The registry.</returns>
    private static ProviderRegistry Relay(
        IReadOnlyList<ProbeProvider>? adapters = null,
        IReadOnlyList<string>? families = null,
        MaxTokensCeiling? ceiling = null,
        Func<DateTimeOffset>? now = null)
    {
        var composed = adapters ?? [new ProbeProvider(ProviderFamilies.Anthropic)];
        var resolving = families ?? [ProviderFamilies.Anthropic];

        var names = resolving
            .Select(ProviderFamilies.DefaultEnvironmentVariable)
            .Where(name => name is not null)
            .ToList();

        return new ProviderRegistry(
            new InMemoryProviderDeclarations(),
            new ProviderCredentials(
                name => names.Contains(name, StringComparer.Ordinal) ? "a-secret" : null,
                _ => null),
            new ProviderAdapters(
                [.. composed.Select(adapter => new KeyValuePair<string, IModelProvider>(adapter.Id.Value, adapter))]),
            ceiling,
            now);
    }

    /// <summary>A declaration an operator would write, applied as <c>office</c>.</summary>
    /// <param name="models">The models it names.</param>
    /// <param name="budgets">The ceilings it declares.</param>
    /// <returns>The declaration.</returns>
    private static ProviderDeclaration Declaration(
        ProviderModels? models = null,
        ProviderBudgets? budgets = null) => new()
    {
        Name = "office",
        Provider = ProviderId.Anthropic,
        Models = models ?? new ProviderModels { Complete = ["office-model"] },
        Credential = CredentialReference.ForEnvironment("MUNARIUM_SECRET_ANTHROPIC"),
        Budgets = budgets ?? ProviderBudgets.None,
    };

    /// <summary>A clock a test moves by hand, so a window is a rule rather than a wait.</summary>
    private sealed class Clock
    {
        private DateTimeOffset _now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

        /// <summary>Reads the current instant.</summary>
        /// <returns>The instant.</returns>
        public DateTimeOffset Now() => _now;

        /// <summary>Moves the clock forward.</summary>
        /// <param name="span">How far to move it.</param>
        public void Advance(TimeSpan span) => _now = _now.Add(span);
    }
}
