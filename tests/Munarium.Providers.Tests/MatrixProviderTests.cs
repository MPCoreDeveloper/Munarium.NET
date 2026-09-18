namespace Munarium.Providers.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Munarium.Evidence;

/// <summary>
/// The semantic plane over REST: what it refuses without asking, and what it makes of an answer.
/// </summary>
/// <remarks>
/// The stub is the peer, so these tests hold both halves of the boundary: that a question the profile cannot put is
/// refused rather than sent, and that an answer the contract defines is read the way the contract says.
/// </remarks>
public class MatrixProviderTests
{
    [Fact]
    public void ItServesOnlyViewsThisProfileDeclares()
    {
        var provider = Provider(Unreached());

        Assert.Equal("matrix", provider.Id);
        Assert.True(provider.CanServe("matrix:revenue_by_region"));

        // A collection is not a data view.
        Assert.False(provider.CanServe("contracts"));
        Assert.False(provider.CanServe("matrix:a_view_nobody_declared"));
    }

    [Fact]
    public async Task AnUnboundLayerRefusesRatherThanCallingAnything()
    {
        var handler = Unreached();
        var provider = Provider(handler);

        var refusal = RefusalOf(await provider.FetchAsync(Layer(["contracts"]), Intent()));

        Assert.Equal(EvidenceRefusalCodes.SourceNotBound, refusal.Code);

        // Reaching the network would have been the bug, so the stub throws if it is ever asked.
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task AnOpenCircuitRefusesWithoutACall()
    {
        var breaker = new CircuitBreaker(1, TimeSpan.FromMinutes(1));
        breaker.RecordFailure();

        var handler = Unreached();
        var provider = Provider(handler, breaker);

        var refusal = RefusalOf(await provider.FetchAsync(Layer(["matrix:revenue_by_region"]), Intent()));

        Assert.Equal(EvidenceRefusalCodes.SourceCircuitOpen, refusal.Code);

        // Named here: the profile's author pinned this view, so the hidden-source rule does not apply to it.
        Assert.Equal("revenue_by_region", refusal.Source);

        // The point of the breaker is that an outage costs no call at all, not a fast failure.
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ASemanticViewWithNoSelectionRefuses()
    {
        var handler = Unreached();
        var provider = Provider(handler);

        var refusal = RefusalOf(await provider.FetchAsync(Layer(["matrix:semantic_view"]), Intent()));

        Assert.Equal(EvidenceRefusalCodes.IntentUnresolved, refusal.Code);
        Assert.Equal("semantic_view", refusal.Source);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ARefusedRequestIsNotAnOutage()
    {
        var handler = Answering(HttpStatusCode.Forbidden, """{"refusal":{"code":"column_not_permitted"}}""");
        var breaker = new CircuitBreaker();
        var provider = Provider(handler, breaker);

        var refusal = RefusalOf(await provider.FetchAsync(Layer(["matrix:revenue_by_region"]), Intent()));

        // The plane's code survives, because the code is what an operator acts on.
        Assert.Equal("column_not_permitted", refusal.Code);

        // A 4xx is the plane answering correctly. Tripping on a policy refusal would take out every other view because
        // one of them is governed the way it should be.
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public async Task AServerErrorTripsTheBreaker()
    {
        var handler = Answering(HttpStatusCode.ServiceUnavailable, "{}");
        var breaker = new CircuitBreaker();
        var provider = Provider(handler, breaker);

        var refusal = RefusalOf(await provider.FetchAsync(Layer(["matrix:revenue_by_region"]), Intent()));

        // No typed refusal in the body, so the request is reported as rejected rather than as a decline - and the
        // breaker counts it as the outage the status says it is.
        Assert.Equal(EvidenceRefusalCodes.SourceRequestRejected, refusal.Code);
        Assert.Equal(1, breaker.ConsecutiveFailures);
    }

    [Fact]
    public async Task AnUnreachablePlaneRefuses()
    {
        var provider = Provider(Failing());

        var refusal = RefusalOf(await provider.FetchAsync(Layer(["matrix:revenue_by_region"]), Intent()));

        Assert.Equal(EvidenceRefusalCodes.SourceUnavailable, refusal.Code);
        Assert.Equal("revenue_by_region", refusal.Source);
    }

    [Fact]
    public async Task ADeadlineThatPassesRefusesAsATimeout()
    {
        var provider = Provider(Slow());

        var layer = Layer(["matrix:revenue_by_region"]) with { DeadlineMilliseconds = 1 };

        var refusal = RefusalOf(await provider.FetchAsync(layer, Intent()));

        // The deadline and not the caller: "the plane is slow" and "nobody is waiting any more" are different answers.
        Assert.Equal(EvidenceRefusalCodes.SourceTimeout, refusal.Code);
    }

    [Fact]
    public async Task AnAnswerBecomesATableWithThePlanesOwnRowIds()
    {
        var handler = Answering(
            HttpStatusCode.OK,
            File.ReadAllText(Path.Combine(Examples(), "evidence-block.complete-table.json")));

        var provider = Provider(handler);

        var table = TableOf(await provider.FetchAsync(Layer(["matrix:revenue_by_region"]), Intent()));

        Assert.Equal(["AMER", "APAC", "EMEA"], table.RowIds);
        Assert.Equal("1180250.50", table.Rows[1][1]);

        var call = Assert.Single(handler.Calls);

        // The contract and not the view: the view-to-contract mapping is the profile's, and what reaches the wire is
        // the contract it resolved to.
        Assert.Equal("http://matrix.test/v1/contracts/open-pipeline-by-region@2/execute", call.Url);
        Assert.Equal("u-1", call.Uid);

        // The turn's question is never part of the request. A contract is executed with typed parameters, which is what
        // makes an injection structurally impossible here rather than merely defended against.
        Assert.DoesNotContain("what do we know", call.Body, StringComparison.Ordinal);
    }

    private static MatrixProvider Provider(StubHandler handler, CircuitBreaker? breaker = null) =>
        new(new HttpClient(handler), "http://matrix.test", Authorization(), Views(), breaker);

    /// <summary>The data views a profile declares, by the name a layer pins.</summary>
    private static IReadOnlyDictionary<string, BoundDataView> Views() =>
        new Dictionary<string, BoundDataView>(StringComparer.Ordinal)
        {
            ["revenue_by_region"] = new()
            {
                Contract = "open-pipeline-by-region@2",
                Kind = DataViewKind.Contract,
                AccessLevel = 2,
            },
            ["semantic_view"] = new()
            {
                Contract = "pipeline-by-region",
                Kind = DataViewKind.DataView,
                AccessLevel = 1,
            },
        };

    private static SessionAuthorization Authorization() => new()
    {
        Tenant = "acme",
        Uid = "u-1",
        AccessLevel = 3,
        Compartments = ["sales"],
        SessionId = "ses-1",
        RunbookRef = "rb@1",
    };

    private static EvidenceLayer Layer(IReadOnlyList<string> sources) => new()
    {
        Name = "revenue",
        Sources = sources,
        Requirement = LayerRequirement.Required,
        Role = AnswerRole.Controlling,
    };

    private static QueryIntent Intent() => new() { Question = "what do we know about the pipeline" };

    /// <summary>A peer that fails the test if it is reached at all.</summary>
    private static StubHandler Unreached() => new((_, _) =>
        throw new InvalidOperationException("the plane was reached when it should not have been"));

    private static StubHandler Failing() => new((_, _) => throw new HttpRequestException("unreachable"));

    /// <summary>A plane that answers too late, so the layer's deadline is what ends the call.</summary>
    private static StubHandler Slow() => new(async (_, cancellationToken) =>
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    private static StubHandler Answering(HttpStatusCode status, string body) => new((_, _) =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    private static EvidenceRefusal RefusalOf(EvidenceBlock block) =>
        block is EvidenceRefusal refusal
            ? refusal
            : throw new Xunit.Sdk.XunitException($"expected a refusal, got {block.KindName()}");

    private static TableBlock TableOf(EvidenceBlock block) =>
        block is TableBlock table
            ? table
            : throw new Xunit.Sdk.XunitException($"expected a table, got {block.KindName()}");

    /// <summary>Finds the contract's examples by walking up from the test binary to the repository root.</summary>
    private static string Examples()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "contract", "matrix", "examples");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("contract/matrix/examples was not found above the test binary.");
    }

    /// <summary>A peer that records what it was asked, so a test can hold the request as well as the answer.</summary>
    /// <param name="answer">What the plane does with a request.</param>
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer = answer;

        /// <summary>Gets the requests this peer received.</summary>
        internal List<(string Url, string Body, string Uid)> Calls { get; } = [];

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Calls.Add((
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.TryGetValues("X-Munarium-Uid", out var uids)
                    ? string.Join(",", uids)
                    : string.Empty));

            return await _answer(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
