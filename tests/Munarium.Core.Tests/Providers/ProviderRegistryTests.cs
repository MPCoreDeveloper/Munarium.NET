namespace Munarium.Core.Tests.Providers;

using Munarium.Core.Tests.Support;
using Munarium.Providers;

/// <summary>
/// The provider plane: what a deployment applied, what a tier resolves to, and what a probe observes.
/// </summary>
/// <remarks>
/// The credential resolver is injected rather than read from the process, because what is under test is the plane's
/// answer to a key that is there and to one that is not - and a test that set a process-wide variable to say so would be
/// deciding it for every test running beside it.
/// </remarks>
public class ProviderRegistryTests
{
    private const string Tenant = "acme";

    /// <summary>Anthropic's built-in capable model, which is what a declaration that names none resolves to.</summary>
    private const string AnthropicCapable = "claude-sonnet-5";

    [Fact]
    public async Task WhatIsAppliedIsListedFirstAndTheDefaultsStandBehindIt()
    {
        var registry = Registry();

        await registry.ApplyAsync(Tenant, Cloud("office"));

        var listed = await registry.ListAsync(Tenant);

        Assert.Equal(
            ["office", "default-anthropic", "default-openai", "default-openrouter"],
            listed.Select(provider => provider.Name));

        Assert.Equal(ProviderRegistry.AppliedSource, listed[0].Source);
        Assert.Equal(ProviderFamilies.Anthropic, listed[0].Provider);

        // No key resolves in this registry, so the plane says so rather than assuming one.
        Assert.False(listed[0].CredentialOk);
        Assert.Equal(ProviderRegistry.DefaultSource, listed[1].Source);
        Assert.Equal(AnthropicCapable, listed[1].Capable);
        Assert.All(listed, provider => Assert.False(provider.CredentialOk));
    }

    [Fact]
    public async Task ATierResolvesThroughTheConfigurationAndThenTheFamilyTable()
    {
        var registry = Registry();

        await registry.ApplyAsync(
            Tenant,
            Cloud("office") with
            {
                Models = new ProviderModels { Fast = "claude-haiku-local", Complete = [AnthropicCapable] },
            });

        var office = (await registry.ListAsync(Tenant))[0];

        Assert.Equal("claude-haiku-local", office.Fast);
        Assert.Equal(AnthropicCapable, office.Capable);
        Assert.Equal("claude-fable-5-1", office.Frontier);
    }

    [Fact]
    public async Task TheReservedNameIsRefusedRatherThanApplied()
    {
        var registry = Registry();

        var refused = await registry.ApplyAsync(Tenant, Cloud(ProviderRegistry.DefaultSelector));

        Assert.Equal(
            "the config name 'default' is reserved for the default-provider rule",
            Refusal(refused));

        var listed = await registry.ListAsync(Tenant);

        Assert.DoesNotContain(listed, provider => provider.Source == ProviderRegistry.AppliedSource);
    }

    [Fact]
    public async Task ACloudFamilyWithoutACredentialReferenceIsRefused()
    {
        var registry = Registry();

        var refused = await registry.ApplyAsync(Tenant, Cloud("office") with { Credential = null });

        Assert.Equal("credentialRef is required for this provider", Refusal(refused));
    }

    [Fact]
    public async Task ADialectThisPortDoesNotKnowIsRefused()
    {
        var registry = Registry();

        var refused = await registry.ApplyAsync(
            Tenant,
            Cloud("office") with { Provider = new ProviderId("gemini"), Credential = null });

        Assert.Equal(
            "unsupported provider 'gemini' (anthropic|openai|openrouter|ollama)",
            Refusal(refused));
    }

    [Fact]
    public async Task ProbingANameNothingIsHeldUnderSaysSo()
    {
        var outcome = await Registry().HealthAsync(Tenant, "nobody");

        Assert.True(outcome is UnknownProviderConfig);
    }

    [Fact]
    public async Task AProbeWithoutACredentialNamesWhereTheKeyShouldBe()
    {
        var registry = Registry();

        await registry.ApplyAsync(Tenant, Cloud("office"));

        var probe = Probe(await registry.HealthAsync(Tenant, "office"));

        Assert.False(probe.Healthy);
        Assert.Equal(ProviderFamilies.Anthropic, probe.Family);
        Assert.Contains("MUNARIUM_SECRET_ANTHROPIC", probe.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeOfAFamilyThisDeploymentHoldsNoAdapterForNamesTheAbsence()
    {
        var registry = Registry(held: ProviderFamilies.Anthropic);

        await registry.ApplyAsync(Tenant, Cloud("office"));

        var probe = Probe(await registry.HealthAsync(Tenant, "office"));

        Assert.False(probe.Healthy);
        Assert.Contains("holds no 'anthropic' adapter", probe.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeThroughAnAdapterReportsWhatItObserved()
    {
        var registry = Compose(
            new ProbeProvider(ProviderFamilies.Anthropic, fingerprint: "abc123"),
            [ProviderFamilies.Anthropic]);

        await registry.ApplyAsync(Tenant, Cloud("office"));

        var probe = Probe(await registry.HealthAsync(Tenant, "office"));

        Assert.True(probe.Healthy);
        Assert.Equal("abc123", probe.EndpointFingerprint);
        Assert.Equal("anthropic answered", probe.Detail);
    }

    [Fact]
    public async Task AProbeWithNoFingerprintToReportStillFingerprintsTheEndpoint()
    {
        var registry = Compose(new ProbeProvider(ProviderFamilies.Anthropic), [ProviderFamilies.Anthropic]);

        await registry.ApplyAsync(Tenant, Cloud("office") with { Endpoint = "https://anthropic.example" });

        var probe = Probe(await registry.HealthAsync(Tenant, "office"));

        var elsewhere = Compose(new ProbeProvider(ProviderFamilies.Anthropic), [ProviderFamilies.Anthropic]);

        await elsewhere.ApplyAsync(Tenant, Cloud("office") with { Endpoint = "https://elsewhere.example" });

        var other = Probe(await elsewhere.HealthAsync(Tenant, "office"));

        Assert.Equal(16, probe.EndpointFingerprint.Length);
        Assert.NotEqual(probe.EndpointFingerprint, other.EndpointFingerprint);
    }

    [Fact]
    public async Task AProbeThatThrowsIsAProbeThatFailed()
    {
        var registry = Compose(
            new ProbeProvider(
                ProviderFamilies.Anthropic,
                failure: new InvalidOperationException("the endpoint refused the key")),
            [ProviderFamilies.Anthropic]);

        await registry.ApplyAsync(Tenant, Cloud("office"));

        var probe = Probe(await registry.HealthAsync(Tenant, "office"));

        Assert.False(probe.Healthy);
        Assert.Equal("the endpoint refused the key", probe.Detail);
    }

    [Fact]
    public async Task TheBuiltInTiersAreProbedAndSkippedWithoutACredential()
    {
        var checks = await Registry().ProbeAllAsync(Tenant);

        // Three reachable families, three tiers each.
        Assert.Equal(9, checks.Count);
        Assert.All(checks, check => Assert.True(check.Skipped));
        Assert.All(checks, check => Assert.False(check.Ok));
        Assert.All(checks, check => Assert.Null(check.LatencyMs));
        Assert.Contains(checks, check => check is { Family: ProviderFamilies.Anthropic, Tier: "capable" });

        // Nothing ran, so there is nothing to be well: a plane with no credential is not vacuously healthy.
        Assert.False(ProviderRegistry.PlaneHealthy(checks));
    }

    [Fact]
    public async Task WhatRunsIsWhatDecidesWhetherThePlaneIsHealthy()
    {
        var registry = Compose(new ProbeProvider(ProviderFamilies.Anthropic), [ProviderFamilies.Anthropic]);

        var checks = await registry.ProbeAllAsync(Tenant);

        Assert.Equal(9, checks.Count);
        Assert.Equal(3, checks.Count(check => check is { Skipped: false, Ok: true }));

        // The tiers nobody configured a credential for are skipped, and a skipped check is not a failure.
        Assert.Equal(6, checks.Count(check => check.Skipped));
        Assert.Equal(
            ["fast", "capable", "frontier"],
            checks
                .Where(check => check.Family == ProviderFamilies.Anthropic)
                .Select(check => check.Tier));

        Assert.True(ProviderRegistry.PlaneHealthy(checks));
    }

    [Fact]
    public async Task AFamilyWithNoAdapterIsAProbeThatFailedRatherThanASkip()
    {
        // Every family's credential resolves, and this deployment holds an adapter for one of them: the others are
        // failures rather than skips, because a key that is present with nothing behind it is exactly what an operator
        // has to be told about.
        var registry = Compose(
            new ProbeProvider(ProviderFamilies.OpenAi),
            [ProviderFamilies.Anthropic, ProviderFamilies.OpenAi, ProviderFamilies.OpenRouter]);

        var checks = await registry.ProbeAllAsync(Tenant);

        Assert.Contains(
            checks,
            check => check is { Family: ProviderFamilies.Anthropic, Skipped: false, Ok: false }
                && check.Detail.Contains("holds no 'anthropic' adapter", StringComparison.Ordinal));

        Assert.False(ProviderRegistry.PlaneHealthy(checks));
    }

    [Fact]
    public async Task AnEmptyAnswerIsATruncatedProbeRatherThanAHealthyOne()
    {
        var registry = Compose(
            new ProbeProvider(ProviderFamilies.Anthropic, answer: "   "),
            [ProviderFamilies.Anthropic]);

        var checks = await registry.ProbeAllAsync(Tenant);
        var fast = checks.First(check => check is { Family: ProviderFamilies.Anthropic, Tier: "fast" });

        Assert.False(fast.Ok);
        Assert.False(fast.Skipped);
        Assert.Contains("empty completion", fast.Detail, StringComparison.Ordinal);
        Assert.NotNull(fast.LatencyMs);
    }

    /// <summary>Reads a refusal's reason out of an apply outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The reason.</returns>
    private static string Refusal(ProviderDeclarationOutcome outcome) => outcome switch
    {
        ProviderConfigRefused refused => refused.Reason,
        ProviderDeclaration applied => throw new InvalidOperationException(
            $"'{applied.Name}' was applied rather than refused"),
    };

    /// <summary>Reads what a probe observed, insisting that one was observed at all.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The probe.</returns>
    private static ProviderProbe Probe(ProviderProbeOutcome outcome) => outcome switch
    {
        ProviderProbe probe => probe,
        UnknownProviderConfig unknown => throw new InvalidOperationException(
            $"nothing is held under '{unknown.Name}'"),
    };

    /// <summary>A declaration an operator would write for a cloud family.</summary>
    /// <param name="name">The name to apply it under.</param>
    /// <returns>The declaration.</returns>
    private static ProviderDeclaration Cloud(string name) => new()
    {
        Name = name,
        Provider = ProviderId.Anthropic,
        Credential = CredentialReference.ForEnvironment("MUNARIUM_SECRET_ANTHROPIC"),
    };

    /// <summary>A registry with a resolver that knows no key at all.</summary>
    /// <returns>The registry.</returns>
    private static ProviderRegistry Registry() => Resolving([]);

    /// <summary>A registry whose credential resolves for one family, holding no adapter at all.</summary>
    /// <param name="held">The family whose conventional variable is set.</param>
    /// <returns>The registry.</returns>
    private static ProviderRegistry Registry(string held) => Resolving([held]);

    /// <summary>A registry with one adapter composed and one family's credential resolving.</summary>
    /// <param name="adapter">The adapter this deployment holds.</param>
    /// <param name="resolving">The families whose conventional variable is set.</param>
    /// <returns>The registry.</returns>
    private static ProviderRegistry Compose(ProbeProvider adapter, IReadOnlyList<string> resolving) =>
        new(
            new InMemoryProviderDeclarations(),
            Credentials(resolving),
            new ProviderAdapters([new KeyValuePair<string, IModelProvider>(adapter.Id.Value, adapter)]));

    /// <summary>A registry with no adapter composed.</summary>
    /// <param name="resolving">The families whose conventional variable is set.</param>
    /// <returns>The registry.</returns>
    private static ProviderRegistry Resolving(IReadOnlyList<string> resolving) =>
        new(new InMemoryProviderDeclarations(), Credentials(resolving));

    /// <summary>Reads the environment the way a deployment does, with only these families' variables set.</summary>
    /// <param name="resolving">The families whose conventional variable is set.</param>
    /// <returns>The resolver.</returns>
    private static ProviderCredentials Credentials(IReadOnlyList<string> resolving)
    {
        var names = resolving
            .Select(ProviderFamilies.DefaultEnvironmentVariable)
            .Where(name => name is not null)
            .ToList();

        return new ProviderCredentials(
            name => names.Contains(name, StringComparer.Ordinal) ? "a-secret" : null,
            _ => null);
    }
}
