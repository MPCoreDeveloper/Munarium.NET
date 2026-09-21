namespace Munarium.Server.Tests;

using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Munarium.Budgets;
using Munarium.Wire;
using Munarium.Wire.Generated;

// The contract's ceilings and the generated message of the same name are both in scope here; the test asserts on the
// first and builds the second, so the contract's shape gets a name of its own.
using Budget = Munarium.Budgets.MaxTokensBudget;

/// <summary>
/// The ceiling and the relay over both transports: the ceilings every paid call is held to, and the routes that make a
/// deployment spend its own credential.
/// </summary>
public class ProviderRelayApiTests(MunariumApiFactory factory) : IClassFixture<MunariumApiFactory>
{
    private readonly MunariumApiFactory _factory = factory;

    [Fact]
    public async Task TheCeilingsAreReadAndReplacedAsOneSet()
    {
        using var client = _factory.CreateClient();

        // A composed deployment with no replacement reports the process defaults, with no instant to name.
        var before = await ReadCeilingsAsync(client);

        Assert.Equal(MaxTokensCeiling.EnvironmentSource, before.Source);
        Assert.Null(before.UpdatedAt);
        Assert.Equal(Budget.Builtin.TurnCompletion, before.Budgets.TurnCompletion);

        var asked = before.Budgets with { TurnCompletion = 4096, HealthAiProbe = 1024 };
        var replaced = await client.PostAsJsonAsync("/v1/max-tokens", asked, WireJson.Default.WireMaxTokensBudget);

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        var after = await replaced.Content.ReadFromJsonAsync(WireJson.Default.WireMaxTokens);

        Assert.NotNull(after);
        Assert.Equal(MaxTokensCeiling.TenantSource, after.Source);
        Assert.NotNull(after.UpdatedAt);
        Assert.Equal(4096, after.Budgets.TurnCompletion);
        Assert.Equal(1024, after.Budgets.HealthAiProbe);

        // What a caller reads afterwards is what it wrote, which is the point of reading the ceiling where the calls are
        // made rather than reporting it from somewhere else.
        var read = await ReadCeilingsAsync(client);

        Assert.Equal(4096, read.Budgets.TurnCompletion);
        Assert.Equal(1024, read.Budgets.HealthAiProbe);
        Assert.Equal(MaxTokensCeiling.TenantSource, read.Source);
    }

    [Fact]
    public async Task ACeilingThatCouldNotBeHonouredIsRefusedWithTheNumberAndTheBound()
    {
        using var client = _factory.CreateClient();

        var refused = await client.PostAsJsonAsync(
            "/v1/max-tokens",
            (await ReadCeilingsAsync(client)).Budgets with { TurnCompletion = 32 },
            WireJson.Default.WireMaxTokensBudget);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.Equal("turn_completion must be between 256 and 16384, and it is 32", problem?.Detail);
    }

    [Fact]
    public async Task ARelayInFrontOfTheGateIsRefusedBeforeAnythingIsChosen()
    {
        using var client = _factory.CreateClient();

        // A bearer that is not a capability is refused even though this deployment does not require authorization: the
        // gate is in front of the relay, not beside it.
        using var asked = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/v1/providers/office/complete", UriKind.Relative))
        {
            Content = JsonContent.Create(new WireCompletionQuery("hello"), WireJson.Default.WireCompletionQuery),
        };

        asked.Headers.Add("authorization", "Bearer not-a-capability");

        var refused = await client.SendAsync(asked);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.Equal(MunariumOperations.UnauthorizedProblem, problem?.Type);

        // Without a bearer the deployment's own principal applies, so the call reaches the plane - which reports that
        // nothing is held under that name rather than pretending it answered.
        var unknown = await client.PostAsJsonAsync(
            "/v1/providers/nobody/complete",
            new WireCompletionQuery("hello"),
            WireJson.Default.WireCompletionQuery);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var notHeld = await unknown.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.Equal(MunariumOperations.UnknownProviderConfigProblem, notHeld?.Type);

        // A configuration this deployment does hold is resolved, and the plane then reports that nothing here can call
        // that family: a deployment with no adapter is a deployment that cannot relay, said out loud.
        var applied = await client.PostAsync(
            new Uri("/v1/providers", UriKind.Relative),
            new StringContent(
                "apiVersion: munarium.ioka.io/v1\nkind: ProviderConfig\nmetadata:\n  name: relay-office\nspec:\n"
                + "  provider: anthropic\n  credentialRef:\n    env: MUNARIUM_SECRET_ANTHROPIC\n",
                System.Text.Encoding.UTF8,
                "text/yaml"));

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        var attempted = await client.PostAsJsonAsync(
            "/v1/providers/relay-office/complete",
            new WireCompletionQuery("hello"),
            WireJson.Default.WireCompletionQuery);

        var body = await attempted.Content.ReadAsStringAsync();

        Assert.True(
            attempted.StatusCode == HttpStatusCode.BadGateway,
            $"expected 502, got {(int)attempted.StatusCode}: {body}");

        var unavailable = await attempted.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.Equal(MunariumOperations.ProviderUnavailableProblem, unavailable?.Type);
    }

    [Fact]
    public async Task ARelayedCallNamingAVersionIsRefusedRatherThanMadeUnrecorded()
    {
        using var client = _factory.CreateClient();

        var refused = await client.PostAsJsonAsync(
            "/v1/providers/office/complete",
            new WireCompletionQuery("hello", VersionId: "memory@1"),
            WireJson.Default.WireCompletionQuery);

        var body = await refused.Content.ReadAsStringAsync();

        Assert.True(
            refused.StatusCode == HttpStatusCode.BadRequest,
            $"expected 400, got {(int)refused.StatusCode}: {body}");

        var problem = await refused.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.Contains("invocation-provenance plane is not ported", problem?.Detail, StringComparison.Ordinal);
    }

    /// <summary>Reads the ceilings in force.</summary>
    /// <param name="client">The client to ask.</param>
    /// <returns>The ceilings.</returns>
    private static async Task<WireMaxTokens> ReadCeilingsAsync(HttpClient client)
    {
        var ceilings = await client.GetFromJsonAsync(
            new Uri("/v1/max-tokens", UriKind.Relative),
            WireJson.Default.WireMaxTokens);

        return Assert.IsType<WireMaxTokens>(ceilings);
    }

    [Fact]
    public async Task TheCeilingsCrossTheOtherTransport()
    {
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpClient = _factory.CreateClient() });

        var client = MunariumServiceClient.Create(channel);

        var replaced = await client.ReplaceMaxTokensAsync(new ReplaceMaxTokensRequest
        {
            Body = new Munarium.Wire.Generated.MaxTokensBudget
            {
                TurnCompletion = 3072,
                QueryExpansion = 256,
                CompleteDefault = 1024,
                HealthaiProbe = 512,
                HierarchyClassifier = 32,
                HierarchyIntent = 480,
                RunbookAdvisory = 2048,
                AuthoringAssist = 8192,
            },
        });

        Assert.Equal(3072, replaced.Data.TurnCompletion);
        Assert.Equal(MaxTokensCeiling.TenantSource, replaced.Data.Source);

        var read = await client.GetMaxTokensAsync(new GetMaxTokensRequest());

        Assert.Equal(3072, read.Data.TurnCompletion);
        Assert.Equal(MaxTokensCeiling.TenantSource, read.Data.Source);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.ReplaceMaxTokensAsync(new ReplaceMaxTokensRequest
            {
                Body = new Munarium.Wire.Generated.MaxTokensBudget { TurnCompletion = 32 },
            }));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal("turn_completion must be between 256 and 16384, and it is 32", refused.Status.Detail);
    }

    [Fact]
    public async Task TheRelayResolvesTheSameGateAndRefusesWhatHasNoFaithfulForm()
    {
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpClient = _factory.CreateClient() });

        var client = MunariumServiceClient.Create(channel);
        var headers = new Metadata { { "authorization", "Bearer not-a-capability" } };

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.ProviderCompleteAsync(
                new ProviderCompleteRequest
                {
                    Name = "office",
                    Body = new CompletionQuery { Prompt = "hello" },
                },
                headers));

        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);

        // The embedding relay is JSON-only: an array of numbers inside an array of vectors has no faithful protobuf
        // form, so the generated method refuses by name rather than flattening it into something else.
        var unimplemented = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.ProviderEmbedAsync(new ProviderEmbedRequest
            {
                Name = "office",
                Body = new EmbeddingQuery { Inputs = { "hello" } },
            }));

        Assert.Equal(StatusCode.Unimplemented, unimplemented.StatusCode);
        Assert.Contains("no faithful protobuf form", unimplemented.Status.Detail, StringComparison.Ordinal);

        // The completion relay itself is served: with the deployment principal it reaches the plane, which reports that
        // nothing is held under that name here.
        var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.ProviderCompleteAsync(new ProviderCompleteRequest
            {
                Name = "office",
                Body = new CompletionQuery { Prompt = "hello" },
            }));

        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
    }
}
