namespace Munarium.Server.Tests;

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
                Stream = "grpc-claims",
                Body = Proposal("grpc-claim-1", """{"vendor_id":"g-1","status":"approved"}"""),
            });

            Assert.Equal(ClaimStatus.Accepted, outcome.Data.Status);
            Assert.Equal("vendor@1|vendor_id=g-1", outcome.Data.Lineage);

            var slice = await client.SliceFactsAsync(new SliceFactsRequest { AsOf = 0 });
            var fact = Assert.Single(slice.Data.Facts.Where(f => f.Lineage == "vendor@1|vendor_id=g-1"));

            Assert.Equal(ClaimStatus.Accepted, fact.Status);
            Assert.NotEmpty(slice.Data.Digest);
        });

    [Fact]
    public async Task ARefusedClaimIsRecordedAsDisputedOverGrpcToo() =>
        await WithClient(async client =>
        {
            var outcome = await client.ProposeClaimAsync(new ProposeClaimRequest
            {
                Stream = "grpc-disputed",
                Body = Proposal("grpc-claim-2", """{"vendor_id":"g-2"}"""),
            });

            // Disputed is a recorded outcome over gRPC as well - not an error and not a lost write.
            Assert.Equal(ClaimStatus.Disputed, outcome.Data.Status);
            Assert.Equal("shape", outcome.Data.Gate);
            Assert.Equal(1, outcome.Data.Head);
        });

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

    private static ClaimProposal Proposal(string claimId, string body) => new()
    {
        ClaimId = claimId,
        Shape = "vendor",
        Body = body,
        Statement = "the supplier is north",
        Actor = "tester",
    };

    private async Task WithClient(Func<MunariumServiceClient, Task> body)
    {
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpClient = _factory.CreateClient() });

        await body(MunariumServiceClient.Create(channel));
    }
}
