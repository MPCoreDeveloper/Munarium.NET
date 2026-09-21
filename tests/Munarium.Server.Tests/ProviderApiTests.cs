namespace Munarium.Server.Tests;

using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Munarium.Providers;
using Munarium.Wire;
using Munarium.Wire.Generated;

/// <summary>
/// The provider plane over both transports: declarations applied as the documents they are, and probes that say what a
/// deployment can reach.
/// </summary>
/// <remarks>
/// The deployment under test composes no cloud adapter, which is the honest state of this port: the plane records the
/// declaration and reports, by name, that nothing here can call that family. Both surfaces are asked the same questions
/// because both are adapters over one operation surface - the only thing that may differ is the encoding.
/// </remarks>
public class ProviderApiTests(MunariumApiFactory factory) : IClassFixture<MunariumApiFactory>
{
    private readonly MunariumApiFactory _factory = factory;

    [Fact]
    public async Task AConfigurationIsAppliedAndListedWithWhatItsTiersResolveTo()
    {
        using var client = _factory.CreateClient();

        var applied = await ApplyAsync(client, "office-anthropic", "claude-haiku-local");

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        var answer = await applied.Content.ReadFromJsonAsync(WireJson.Default.WireProviderApplied);

        Assert.Equal("office-anthropic", answer?.ConfigName);

        var listed = await ListAsync(client);
        var office = listed.Providers.Single(provider => provider.Name == "office-anthropic");

        Assert.Equal("anthropic", office.Provider);
        Assert.Equal(ProviderRegistry.AppliedSource, office.Source);
        Assert.Equal("claude-haiku-local", office.Fast);
        Assert.Equal("claude-sonnet-5", office.Capable);
        Assert.Equal("claude-fable-5-1", office.Frontier);

        // The key is not set in the test environment, so the plane says so rather than assuming one.
        Assert.False(office.CredentialOk);

        // The environment-backed defaults stand behind what was applied, and are never stored.
        var names = listed.Providers.Select(provider => provider.Name).ToList();

        Assert.Equal(
            ["default-anthropic", "default-openai", "default-openrouter"],
            listed.Providers
                .Where(provider => provider.Source == ProviderRegistry.DefaultSource)
                .Select(provider => provider.Name));
        Assert.True(names.IndexOf("office-anthropic") < names.IndexOf("default-anthropic"));
    }

    [Fact]
    public async Task ADocumentThatCannotBeReadIsRefusedWhenItIsApplied()
    {
        using var client = _factory.CreateClient();

        var refused = await PostAsync(
            client,
            """
            apiVersion: munarium.ioka.io/v1
            kind: ProviderConfig
            metadata:
              name: office-typo
            spec:
              provider: anthropic
              endpont: https://anthropic.example/v1
              credentialRef:
                env: MUNARIUM_SECRET_ANTHROPIC
            """);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.Equal("spec: unknown field 'endpont'", problem?.Detail);

        // A refused document is not applied, so nothing is held under its name.
        var listed = await ListAsync(client);

        Assert.DoesNotContain(listed.Providers, provider => provider.Name == "office-typo");
    }

    [Fact]
    public async Task AProbeWithoutAKeyNamesWhereItShouldBe()
    {
        using var client = _factory.CreateClient();

        await ApplyAsync(client, "office-no-key", null);

        var response = await client.GetAsync(new Uri("/v1/providers/office-no-key/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var health = await response.Content.ReadFromJsonAsync(WireJson.Default.WireProviderHealth);

        Assert.NotNull(health);
        Assert.False(health.Healthy);
        Assert.Equal("anthropic", health.Provider);
        Assert.Equal(16, health.EndpointFingerprint.Length);
        Assert.Contains("MUNARIUM_SECRET_ANTHROPIC", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeOfANameNothingIsHeldUnderIsANotFound()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/v1/providers/nobody/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.Equal(MunariumOperations.UnknownProviderConfigProblem, problem?.Type);
    }

    [Fact]
    public async Task ThePlaneIsProbedAcrossEveryBuiltInTier()
    {
        using var client = _factory.CreateClient();

        var health = await client.GetFromJsonAsync(
            new Uri("/healthai", UriKind.Relative),
            WireJson.Default.WireHealthAi);

        Assert.NotNull(health);
        Assert.Equal(9, health.Checks.Count);
        Assert.Equal(
            ["anthropic", "openai", "openrouter"],
            health.Checks.Select(check => check.Provider).Distinct(StringComparer.Ordinal));
        Assert.Equal(
            ["fast", "capable", "frontier"],
            health.Checks.Where(check => check.Provider == "anthropic").Select(check => check.Tier));

        // No adapter is composed here, so nothing answered - and a plane where nothing answered is not healthy.
        Assert.DoesNotContain(health.Checks, check => check.Ok);
        Assert.False(health.Healthy);

        // A check is either skipped for want of a key or failed for want of an adapter, and its detail says which.
        Assert.All(health.Checks, check => Assert.False(string.IsNullOrWhiteSpace(check.Detail)));
    }

    [Fact]
    public async Task TheSameDeclarationCrossesTheOtherTransport()
    {
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpClient = _factory.CreateClient() });

        var client = MunariumServiceClient.Create(channel);

        var applied = await client.ApplyProviderAsync(new ApplyProviderRequest
        {
            Body = new ApplyProviderBody
            {
                Value =
                    """
                    apiVersion: munarium.ioka.io/v1
                    kind: ProviderConfig
                    metadata:
                      name: office-anthropic-grpc
                    spec:
                      provider: anthropic
                      models:
                        fast: claude-haiku-local
                      credentialRef:
                        env: MUNARIUM_SECRET_ANTHROPIC
                    """,
            },
        });

        Assert.Equal("office-anthropic-grpc", applied.Data.ConfigName);

        var listed = await client.ListProvidersAsync(new ListProvidersRequest());
        var office = listed.Data.Providers.Single(provider => provider.Name == "office-anthropic-grpc");

        Assert.Equal("anthropic", office.Provider);
        Assert.Equal("claude-haiku-local", office.Fast);
        Assert.Equal("claude-sonnet-5", office.Capable);

        var health = await client.ProviderHealthAsync(new ProviderHealthRequest { Name = "office-anthropic-grpc" });

        Assert.False(health.Data.Healthy);
        Assert.Contains("MUNARIUM_SECRET_ANTHROPIC", health.Data.Detail, StringComparison.Ordinal);

        var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.ProviderHealthAsync(new ProviderHealthRequest { Name = "nobody" }));

        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AConfigurationThatCannotBeReadIsRefusedOverTheOtherTransportToo()
    {
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpClient = _factory.CreateClient() });

        var client = MunariumServiceClient.Create(channel);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.ApplyProviderAsync(new ApplyProviderRequest
            {
                Body = new ApplyProviderBody
                {
                    Value = "apiVersion: munarium.ioka.io/v1\nkind: Runbook\nmetadata:\n  name: x\n",
                },
            }));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal("kind must be ProviderConfig, got 'Runbook'", refused.Status.Detail);
    }

    /// <summary>Reads the plane as an operator does.</summary>
    /// <param name="client">The client to ask.</param>
    /// <returns>The provider plane.</returns>
    private static async Task<WireProviderList> ListAsync(HttpClient client)
    {
        var listed = await client.GetFromJsonAsync(
            new Uri("/v1/providers", UriKind.Relative),
            WireJson.Default.WireProviderList);

        return Assert.IsType<WireProviderList>(listed);
    }

    /// <summary>Applies an Anthropic configuration, naming the tier override when there is one.</summary>
    /// <param name="client">The client to ask.</param>
    /// <param name="name">The name to apply it under.</param>
    /// <param name="fast">The fast tier override, or <see langword="null"/> for the family's built-in.</param>
    /// <returns>The response, for the test to assert on.</returns>
    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, string name, string? fast)
    {
        var models = fast is null ? string.Empty : $"  models:\n    fast: {fast}\n";

        return PostAsync(
            client,
            "apiVersion: munarium.ioka.io/v1\n"
            + "kind: ProviderConfig\n"
            + "metadata:\n"
            + $"  name: {name}\n"
            + "spec:\n"
            + "  provider: anthropic\n"
            + models
            + "  credentialRef:\n"
            + "    env: MUNARIUM_SECRET_ANTHROPIC\n");
    }

    /// <summary>Posts a document as the YAML it is.</summary>
    /// <param name="client">The client to ask.</param>
    /// <param name="yaml">The document.</param>
    /// <returns>The response, for the test to assert on.</returns>
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string yaml)
    {
        var body = new StringContent(yaml, System.Text.Encoding.UTF8, "text/yaml");

        return client.PostAsync(new Uri("/v1/providers", UriKind.Relative), body);
    }
}
