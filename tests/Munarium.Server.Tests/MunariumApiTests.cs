namespace Munarium.Server.Tests;

using Munarium.Wire;

/// <summary>
/// The JSON/HTTP surface of the wire contract, exercised over the real application: a real kernel,
/// a real SharpCoreDB database, and the contract's own shapes.
/// </summary>
/// <remarks>
/// Every test writes to a stream of its own, so the answers do not depend on the order xunit happens
/// to run them in. Pinned reads are asserted relatively - same pin, same digest - for the same
/// reason.
/// </remarks>
public class MunariumApiTests(MunariumApiFactory factory) : IClassFixture<MunariumApiFactory>
{
    private const string VendorShape = "vendor";

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task TheHealthCheckNamesTheContractItSpeaks()
    {
        var health = await GetAsync("/healthz", WireJson.Default.WireHealth);

        Assert.Equal("ok", health.Status);
        Assert.Equal(MunariumOperations.Contract, health.Contract);
    }

    [Fact]
    public async Task AStreamThatHasNotBeenWrittenToHasHeadZero()
    {
        var head = await GetAsync("/v1/streams/empty-stream/head", WireJson.Default.WireStreamHead);

        Assert.Equal("empty-stream", head.Stream);
        Assert.Equal(0, head.Head);
    }

    [Fact]
    public async Task AnAcceptedClaimEchoesTheLineageTheKernelDerived()
    {
        var outcome = await ProposeAsync("stream-accepted", "claim-accepted", Vendor("v-1"));

        Assert.Equal(WireClaimStatus.Accepted, outcome.Status);
        Assert.Equal("vendor@1|vendor_id=v-1", outcome.Lineage);
        Assert.Equal(1, outcome.Head);
        Assert.Equal(string.Empty, outcome.Gate);
    }

    [Fact]
    public async Task AClaimThatViolatesItsShapeIsRecordedAsDisputedWithTheReason()
    {
        var outcome = await ProposeAsync("stream-disputed", "claim-disputed", """{"vendor_id":"v-2"}""");

        Assert.Equal(WireClaimStatus.Disputed, outcome.Status);
        Assert.Equal("shape", outcome.Gate);
        Assert.Contains("required property 'status'", outcome.Reason, StringComparison.Ordinal);

        // Disputed is a recorded outcome, not a lost write: the head moved.
        Assert.Equal(1, outcome.Head);
    }

    [Fact]
    public async Task AClaimUnderAnUnknownShapeIsRecordedAsDisputedRatherThanRefused()
    {
        var outcome = await ProposeAsync(
            "stream-unknown-shape",
            new WireClaimProposal("claim-unknown", "patent", """{"patent_id":"p-1"}""", "a claim", "tester"));

        Assert.Equal(WireClaimStatus.Disputed, outcome.Status);
        Assert.Equal("shape", outcome.Gate);
        Assert.Equal("patent@unregistered", outcome.Lineage);
    }

    [Fact]
    public async Task APinIsReproducibleAndALaterWriteDoesNotChangeIt()
    {
        await ProposeAsync("stream-pin", "claim-pin-1", Vendor("v-7"));

        // Pin at the state the first claim produced, then correct it and read both positions back.
        var beforeCorrection = await GetAsync("/v1/facts?as_of=0", WireJson.Default.WireFactSlice);
        var pin = beforeCorrection.AsOf;

        var corrected = (await ProposeAsync("stream-pin", "claim-pin-2", Vendor("v-7", "pending"))).Head;

        var atPin = await GetAsync($"/v1/facts?as_of={pin}", WireJson.Default.WireFactSlice);
        var now = await GetAsync("/v1/facts?as_of=0", WireJson.Default.WireFactSlice);

        // A correction does not rewrite history: the earlier pin digests the same and still shows the
        // earlier claim, while the present sees the correction.
        Assert.Equal(pin, atPin.AsOf);
        Assert.Equal(beforeCorrection.Digest, atPin.Digest);
        Assert.NotEqual(beforeCorrection.Digest, now.Digest);
        Assert.Equal("claim-pin-1", Assert.Single(atPin.Facts, fact => fact.Lineage == "vendor@1|vendor_id=v-7").ClaimId);
        Assert.Equal("claim-pin-2", Assert.Single(now.Facts, fact => fact.Lineage == "vendor@1|vendor_id=v-7").ClaimId);
        Assert.Equal(2, corrected);
    }

    [Fact]
    public async Task TheShapesAreServedInTheContractsForm()
    {
        var shapes = await GetAsync("/v1/shapes", WireJson.Default.WireShapeList);

        var vendor = Assert.Single(shapes.Shapes.Where(shape => shape.Name == VendorShape));

        Assert.Equal(1, vendor.Version);
        Assert.Equal(["vendor_id"], vendor.Identity);
        Assert.Contains("\"required\"", vendor.Schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAlwaysAnswersWithTheEnvelope()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/search",
            new WireSearchQuery("north", 5),
            WireJson.Default.WireSearchQuery);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync(WireJson.Default.WireSearchResult);

        Assert.NotNull(result);
        Assert.Equal("munarium@1", result.Envelope.IndexVersion);
    }

    [Fact]
    public async Task AProposalMissingWhatTheContractRequiresIsRefusedAsAClientError()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/streams/stream-invalid/claims",
            new WireClaimProposal(string.Empty, VendorShape, Vendor("v-9"), "a claim", "tester"),
            WireJson.Default.WireClaimProposal);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.NotNull(problem);
        Assert.Equal("claim_id is required.", problem.Detail);
    }

    [Fact]
    public async Task TheJsonUsesTheFieldNamesTheContractDeclares()
    {
        await ProposeAsync("stream-json", "claim-json", Vendor("v-json"));

        var response = await _client.GetAsync(new Uri("/v1/facts?as_of=0", UriKind.Relative));
        var json = await response.Content.ReadAsStringAsync();

        // The contract spells these this way, and no transport gets its own idea about that.
        Assert.Contains("\"as_of\"", json, StringComparison.Ordinal);
        Assert.Contains("\"digest\"", json, StringComparison.Ordinal);
        Assert.Contains("\"claim_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lineage\"", json, StringComparison.Ordinal);
        Assert.Contains("vendor@1|vendor_id=v-json", json, StringComparison.Ordinal);
    }

    private static string Vendor(string vendorId, string status = "approved") =>
        $$"""{"vendor_id":"{{vendorId}}","status":"{{status}}"}""";

    private async Task<WireClaimOutcome> ProposeAsync(string stream, string claimId, string body) =>
        await ProposeAsync(
            stream,
            new WireClaimProposal(claimId, VendorShape, body, "the supplier is north", "tester"));

    private async Task<WireClaimOutcome> ProposeAsync(string stream, WireClaimProposal proposal)
    {
        var response = await _client.PostAsJsonAsync(
            $"/v1/streams/{stream}/claims",
            proposal,
            WireJson.Default.WireClaimProposal);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync(WireJson.Default.WireClaimOutcome))!;
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type)
    {
        var response = await _client.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync(type))!;
    }
}
