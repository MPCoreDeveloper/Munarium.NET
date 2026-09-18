namespace Munarium.Server.Tests;

using Grpc.Core;
using Grpc.Net.Client;
using Munarium.Wire;
using Munarium.Wire.Generated;

/// <summary>
/// The gRPC surface, exercised through the client SharpPortico generated from the same specification
/// the JSON surface implements.
/// </summary>
/// <remarks>
/// This is the conformance check the wire layer exists for. Both surfaces answer from one
/// <see cref="MunariumOperations"/>, so the same question asked over either transport has to produce
/// the same answer - and the channels here go through the real application, not a stand-in.
/// </remarks>
public class GrpcSurfaceTests(MunariumApiFactory factory) : IClassFixture<MunariumApiFactory>
{
    private readonly MunariumApiFactory _factory = factory;

    [Fact]
    public async Task TheGrpcSurfaceAnswersWithTheContractItSpeaks() =>
        await WithClient(async client =>
        {
            var response = await client.GetHealthAsync(new GetHealthRequest());

            Assert.Equal("ok", response.Data.Status);
            Assert.Equal(MunariumOperations.Contract, response.Data.Contract);
        });

    [Fact]
    public async Task AClaimAndTheFactsItProducedTravelOverGrpc() =>
        await WithClient(async client =>
        {
            var outcome = await client.ProposeClaimAsync(new ProposeClaimRequest
            {
                VersionId = "grpc-claims",
                Body = Proposal("grpc-claim-1", """{"vendor_id":"g-1","status":"approved"}"""),
            });

            Assert.Equal(ClaimStatus.Accepted, outcome.Data.Status);
            Assert.Equal(ClaimType.Fact, outcome.Data.ClaimType);
            Assert.Equal("vendor@1|vendor_id=g-1", outcome.Data.Lineage);

            var slice = await client.SliceFactsAsync(
                new SliceFactsRequest { AsOf = 0, VersionId = "grpc-claims" });

            var fact = Assert.Single(slice.Data.Facts.Where(f => f.Lineage == "vendor@1|vendor_id=g-1"));

            Assert.Equal(ClaimStatus.Accepted, fact.Status);
            Assert.Equal("grpc-claims", fact.VersionId);
            Assert.NotEmpty(slice.Data.Digest);
        });

    [Fact]
    public async Task ARefusedClaimIsRecordedAsDisputedOverGrpcToo() =>
        await WithClient(async client =>
        {
            var outcome = await client.ProposeClaimAsync(new ProposeClaimRequest
            {
                VersionId = "grpc-disputed",
                Body = Proposal("grpc-claim-2", """{"vendor_id":"g-2"}"""),
            });

            // Disputed is a recorded outcome over gRPC as well - not an error and not a lost write.
            Assert.Equal(ClaimStatus.Disputed, outcome.Data.Status);
            Assert.Equal("shape", outcome.Data.Gate);
            Assert.Equal(1, outcome.Data.Head);
        });

    [Fact]
    public async Task AVersionsLineageTravelsOverGrpc() =>
        await WithClient(async client =>
        {
            await client.CreateVersionAsync(new CreateVersionRequest
            {
                Body = new VersionRequest { VersionId = "grpc-lineage-1", AsOfDate = "2026-08-01", Actor = "tester" },
            });

            await client.CreateVersionAsync(new CreateVersionRequest
            {
                Body = new VersionRequest
                {
                    VersionId = "grpc-lineage-2",
                    ParentVersionId = "grpc-lineage-1",
                    Actor = "tester",
                },
            });

            var lineage = await client.GetLineageAsync(new GetLineageRequest { VersionId = "grpc-lineage-2" });

            Assert.Equal(
                ["grpc-lineage-1", "grpc-lineage-2"],
                lineage.Data.Versions.Select(version => version.VersionId));
            Assert.Equal("2026-08-01", lineage.Data.Versions[0].AsOfDate);
        });

    [Fact]
    public async Task AnUnknownVersionIsNotFoundOverGrpc() =>
        await WithClient(async client =>
        {
            var failure = await Assert.ThrowsAsync<RpcException>(
                () => client.GetLineageAsync(new GetLineageRequest { VersionId = "grpc-no-such-version" }));

            Assert.Equal(StatusCode.NotFound, failure.StatusCode);
        });

    [Fact]
    public async Task TheSameQuestionOverBothTransportsComposesTheSameContext()
    {
        await WithClient(async client =>
        {
            await client.ProposeClaimAsync(new ProposeClaimRequest
            {
                VersionId = "grpc-context",
                Body = Proposal("grpc-context-1", """{"vendor_id":"g-context","status":"approved"}"""),
            });

            var overGrpc = await client.ComposeContextAsync(new ComposeContextRequest
            {
                Body = new ContextRequest { VersionId = "grpc-context" },
            });

            var overJson = await ComposeOverJsonAsync("grpc-context");

            // One specification, one implementation, two encodings: the composed text, and the hash that
            // stands for it, cannot differ between transports.
            Assert.Equal(overJson.Text, overGrpc.Data.Text);
            Assert.Equal(overJson.ContentHash, overGrpc.Data.ContentHash);
            Assert.Equal(overJson.EstimatedTokens, overGrpc.Data.EstimatedTokens);
        });
    }

    [Fact]
    public async Task TheShapesTravelOverGrpcAsTheyDoOverJson() =>
        await WithClient(async client =>
        {
            var shapes = await client.ListShapesAsync(new ListShapesRequest());
            var vendor = Assert.Single(shapes.Data.Shapes.Where(shape => shape.Name == "vendor"));

            Assert.Equal(1, vendor.Version);
            Assert.Equal(["vendor_id"], vendor.Identity);
        });

    [Fact]
    public async Task SearchAnswersWithTheEnvelopeOverGrpc() =>
        await WithClient(async client =>
        {
            var result = await client.SearchAsync(new SearchRequest
            {
                Body = new SearchQuery { Text = "north", TopK = 5 },
            });

            Assert.Equal("munarium@1", result.Data.Envelope.IndexVersion);
        });

    /// <summary>
    /// The candidate plane over gRPC: a batch is one unit, so one append moves the head however many claims it
    /// carries.
    /// </summary>
    [Fact]
    public async Task ABatchLandsAsOneAppendOverGrpc() =>
        await WithClient(async client =>
        {
            var outcome = await client.ProposeClaimBatchAsync(new ProposeClaimBatchRequest
            {
                VersionId = "grpc-batch-accepted",
                Body = Batch(("service", "api_version", "v2"), ("service", "owner_team", "platform")),
            });

            Assert.Equal(2, outcome.Data.Head);
            Assert.Equal(2, outcome.Data.Claims.Count);
            Assert.All(outcome.Data.Claims, claim => Assert.Equal(ClaimStatus.Accepted, claim.Status));
            Assert.Equal("service.api_version", outcome.Data.Claims[0].Lineage);
            Assert.DoesNotContain(outcome.Data.Findings, finding => finding.Severity == Severity.Block);
        });

    /// <summary>
    /// A blocked claim is recorded as disputed over gRPC too, and the finding that blocked it travels with the
    /// batch - which is what lets a caller say why rather than only that.
    /// </summary>
    [Fact]
    public async Task ABlockedClaimCarriesItsFindingOverGrpc() =>
        await WithClient(async client =>
        {
            await client.ProposeClaimBatchAsync(new ProposeClaimBatchRequest
            {
                VersionId = "grpc-batch-disputed",
                Body = Batch(("service", "api_version", "v1")),
            });

            var second = await client.ProposeClaimBatchAsync(new ProposeClaimBatchRequest
            {
                VersionId = "grpc-batch-disputed",
                Body = Batch(("service", "api_version", "v2")),
            });

            var claim = Assert.Single(second.Data.Claims);
            var finding = Assert.Single(second.Data.Findings, item => item.Severity == Severity.Block);

            Assert.Equal(ClaimStatus.Disputed, claim.Status);
            Assert.Equal("service.api_version", claim.Lineage);
            Assert.Equal("service.api_version", finding.ClaimKey);
            Assert.StartsWith("gate.", finding.RuleId, StringComparison.Ordinal);

            // The per-claim verdict and the finding are the same judgement, told twice: one cannot disagree
            // with the other without the caller noticing.
            Assert.Equal(finding.RuleId, claim.Gate);
            Assert.Equal(finding.Message, claim.Reason);
        });

    /// <summary>The same batch a JSON caller would send, built for the gRPC surface.</summary>
    private static ClaimBatchRequest Batch(params (string Subject, string Key, string Value)[] claims)
    {
        var body = new ClaimBatchRequest();

        foreach (var (subject, key, value) in claims)
        {
            body.Claims.Add(new ClaimCandidate
            {
                ClaimType = ClaimType.Fact,
                Subject = subject,
                Key = key,
                Value = value,
            });
        }

        return body;
    }

    private static ClaimProposal Proposal(string claimId, string body) => new()
    {
        ClaimId = claimId,
        ClaimType = ClaimType.Fact,
        Shape = "vendor",
        Body = body,
        Statement = "the supplier is north",
        Actor = "tester",
    };

    /// <summary>The same question asked over the other transport, for the conformance comparison.</summary>
    private async Task<WireComposedContext> ComposeOverJsonAsync(string version)
    {
        using var http = _factory.CreateClient();

        var response = await http.PostAsJsonAsync(
            "/v1/context",
            new WireContextRequest(version, string.Empty, 0, string.Empty, 0, 0),
            WireJson.Default.WireContextRequest);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync(WireJson.Default.WireComposedContext))!;
    }

    private async Task WithClient(Func<MunariumServiceClient, Task> body)
    {
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpClient = _factory.CreateClient() });

        await body(MunariumServiceClient.Create(channel));
    }
}
