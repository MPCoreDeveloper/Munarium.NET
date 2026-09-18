namespace Munarium.Server.Tests;

using Grpc.Core;
using Grpc.Net.Client;
using Munarium.Ledger;
using Munarium.Retrieval;
using Munarium.Wire;
using Munarium.Wire.Generated;

// The kernel's source ingest type is not the contract's message of the same name; this file only needs the key.
using SourceKey = Munarium.Sources.SourceKey;

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

            var fact = Assert.Single(slice.Data.Facts, f => f.Lineage == "vendor@1|vendor_id=g-1");

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
            var vendor = Assert.Single(shapes.Data.Shapes, shape => shape.Name == "vendor");

            Assert.Equal(1, vendor.Version);
            Assert.Equal(["vendor_id"], vendor.Identity);
        });

    /// <summary>
    /// The ingest surface over the other transport: the same document, the same row, the same index version - and the
    /// same answer when it is offered twice.
    /// </summary>
    [Fact]
    public async Task ADocumentIngestedOverGrpcIsReadBackOverGrpc() =>
        await WithClient(async client =>
        {
            var request = new IngestSourceRequest
            {
                Body = new SourceIngest
                {
                    Path = "grpc/bell.txt",
                    MediaType = "text/plain",
                    Content = "The Bell rang twice at the north gate.",
                },
            };

            var ingested = await client.IngestSourceAsync(request);

            Assert.Equal("new", ingested.Data.Kind);
            Assert.True(ingested.Data.ChunksIndexed > 0, "the document should have been chunked and indexed");
            Assert.Equal(SourceKey.Id(MunariumKernel.Tenant, "grpc/bell.txt"), ingested.Data.SourceId);
            Assert.NotEmpty(ingested.Data.IndexVersion);

            var info = await client.GetSourceAsync(
                new GetSourceRequest { SourceId = ingested.Data.SourceId });

            Assert.Equal("grpc/bell.txt", info.Data.Path);
            Assert.Equal(ingested.Data.ContentHash, info.Data.ContentHash);
            Assert.Equal("sharpcoredb", info.Data.BackendId);

            // The same bytes again change nothing, over this transport as over the other.
            var again = await client.IngestSourceAsync(request);

            Assert.Equal("unchanged", again.Data.Kind);
            Assert.Equal(0, again.Data.ChunksIndexed);
        });

    /// <summary>
    /// A refusal travels as a refusal over gRPC too, and nothing is stored: the two surfaces are the same contract, so
    /// a document one of them refuses cannot be one the other accepts.
    /// </summary>
    [Fact]
    public async Task ADocumentWithNoExtractorIsRefusedOverGrpc() =>
        await WithClient(async client =>
        {
            var refusal = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.IngestSourceAsync(new IngestSourceRequest
                {
                    Body = new SourceIngest
                    {
                        Path = "grpc/scan.pdf",
                        MediaType = "application/pdf",
                        Content = "%PDF-1.7",
                    },
                }));

            Assert.Equal(StatusCode.InvalidArgument, refusal.StatusCode);
            Assert.Contains("application/pdf", refusal.Status.Detail, StringComparison.Ordinal);
        });

    [Fact]
    public async Task SearchAnswersWithTheEnvelopeOverGrpc() =>
        await WithClient(async client =>
        {
            var result = await client.SearchAsync(new SearchRequest
            {
                Body = new SearchQuery { Text = "north", TopK = 5 },
            });

            // Which version answers depends on what the deployment has built, so the claim is that an answer carries
            // one rather than that it is the version a freshly composed deployment starts with.
            Assert.False(string.IsNullOrWhiteSpace(result.Data.Envelope.IndexVersion));
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

    /// <summary>The findings a batch produced are readable per version over gRPC as well.</summary>
    [Fact]
    public async Task TheFindingsOfABatchTravelOverGrpc() =>
        await WithClient(async client =>
        {
            await client.ProposeClaimBatchAsync(new ProposeClaimBatchRequest
            {
                VersionId = "grpc-findings",
                Body = Batch(("service", "api_version", "v1")),
            });

            await client.ProposeClaimBatchAsync(new ProposeClaimBatchRequest
            {
                VersionId = "grpc-findings",
                Body = Batch(("service", "api_version", "v2")),
            });

            var findings = await client.ListFindingsAsync(new ListFindingsRequest { VersionId = "grpc-findings" });
            var blocked = Assert.Single(findings.Data.Findings, stored => stored.Finding.Severity == Severity.Block);

            Assert.True(blocked.Sequence > 0);
            Assert.Equal("service.api_version", blocked.Finding.ClaimKey);
            Assert.StartsWith("gate.", blocked.Finding.RuleId, StringComparison.Ordinal);
            Assert.NotEmpty(blocked.Finding.Detail);
        });

    /// <summary>One pin, every plane, over the other transport.</summary>
    [Fact]
    public async Task ASnapshotTravelsOverGrpc() =>
        await WithClient(async client =>
        {
            await client.ProposeClaimBatchAsync(new ProposeClaimBatchRequest
            {
                VersionId = "grpc-snapshot",
                Body = Batch(("service", "api_version", "v2")),
            });

            var snapshot = await client.LoadSnapshotAsync(new LoadSnapshotRequest { VersionId = "grpc-snapshot" });
            var fact = Assert.Single(snapshot.Data.Facts);

            Assert.Equal("service", fact.Subject);
            Assert.Equal("api_version", fact.Key);
            Assert.Equal("v2", fact.Value);
            Assert.Equal(ClaimStatus.Accepted, fact.Status);
            Assert.Equal(Provenance.Witnessed, fact.Provenance);
            Assert.NotEmpty(snapshot.Data.Digests);
            Assert.NotEmpty(snapshot.Data.WrittenAt);
            Assert.True(snapshot.Data.AsOfSequence > 0);
        });

    /// <summary>The lock, the release and the promise travel over gRPC as they do over JSON.</summary>
    [Fact]
    public async Task TheAuthoringCommandsTravelOverGrpc() =>
        await WithClient(async client =>
        {
            var locked = await client.LockAnchorAsync(new LockAnchorRequest
            {
                VersionId = "grpc-authoring",
                Body = new AnchorLock
                {
                    Subject = "service",
                    Key = "api_version",
                    Value = "v2",
                    ScopePath = "release",
                },
            });

            Assert.Equal("service.api_version", locked.Data.DetailKey);
            Assert.Equal("v2", locked.Data.LockedValue);
            Assert.Equal(AnchorStatus.Locked, locked.Data.Status);

            var anchors = await client.ListAnchorsAsync(new ListAnchorsRequest { VersionId = "grpc-authoring" });

            Assert.Equal("v2", Assert.Single(anchors.Data.Anchors).LockedValue);

            var released = await client.ReleaseAnchorAsync(new ReleaseAnchorRequest
            {
                VersionId = "grpc-authoring",
                DetailKey = "service.api_version",
            });

            Assert.True(released.Data.Released);
            Assert.Empty((await client.ListAnchorsAsync(new ListAnchorsRequest { VersionId = "grpc-authoring" })).Data.Anchors);

            var opened = await client.OpenPromiseAsync(new OpenPromiseRequest
            {
                VersionId = "grpc-authoring",
                Body = new PromiseRegistration
                {
                    Key = "audit-report",
                    Kind = "deliverable",
                    Description = "an audit report",
                    OriginScope = "release",
                    DueScope = "compliance",
                },
            });

            Assert.Equal(PromiseStatus.Open, opened.Data.Status);

            var promises = await client.ListPromisesAsync(new ListPromisesRequest
            {
                VersionId = "grpc-authoring",
                OverdueScope = "compliance",
                Final = true,
            });

            Assert.Single(promises.Data.Promises);
            Assert.Equal("gate.promise-unfulfilled", Assert.Single(promises.Data.Findings).RuleId);

            var fulfilled = await client.FulfillPromiseAsync(new FulfillPromiseRequest
            {
                VersionId = "grpc-authoring",
                Key = "audit-report",
            });

            Assert.True(fulfilled.Data.Fulfilled);
        });

    /// <summary>A counter and its directives travel over gRPC as they do over JSON.</summary>
    [Fact]
    public async Task ACounterTravelsOverGrpc() =>
        await WithClient(async client =>
        {
            var recorded = await client.RecordCounterAsync(new RecordCounterRequest
            {
                VersionId = "grpc-counter",
                Body = new CounterRecording { Key = "the bell", Total = 7, Budget = 6 },
            });

            Assert.Equal(7, recorded.Data.Total);
            Assert.True(recorded.Data.OverBudget);

            var counters = await client.ListCountersAsync(new ListCountersRequest { VersionId = "grpc-counter" });

            Assert.Equal(7, Assert.Single(counters.Data.Counters).Total);
            Assert.Contains("AVOID: 'the bell'", counters.Data.Directives, StringComparison.Ordinal);
        });

    /// <summary>
    /// A key travels over gRPC the way it travels over JSON - in the body where the command has one, and as a field where
    /// the route carries nothing else - and it is answered rather than done again on both.
    /// </summary>
    [Fact]
    public async Task AKeyedCommandIsAnsweredRatherThanDoneAgainOverGrpc() =>
        await WithClient(async client =>
        {
            string released = LedgerIds.New();

            await client.LockAnchorAsync(new LockAnchorRequest
            {
                VersionId = "grpc-keyed-release",
                Body = new AnchorLock { Subject = "service", Key = "api_version", Value = "v2" },
            });

            var first = await client.ReleaseAnchorAsync(new ReleaseAnchorRequest
            {
                VersionId = "grpc-keyed-release",
                DetailKey = "service.api_version",
                IdempotencyKey = released,
            });

            Assert.True(first.Data.Released);

            // Nothing is locked now, so an unkeyed release would answer "nothing was released": the retry is owed the
            // answer of the attempt that removed the lock.
            var retried = await client.ReleaseAnchorAsync(new ReleaseAnchorRequest
            {
                VersionId = "grpc-keyed-release",
                DetailKey = "service.api_version",
                IdempotencyKey = released,
            });

            Assert.True(retried.Data.Released);

            string counted = LedgerIds.New();

            await client.RecordCounterAsync(new RecordCounterRequest
            {
                VersionId = "grpc-keyed-release",
                Body = new CounterRecording { Key = "the bell", Total = 4, IdempotencyKey = counted },
            });

            // The retry reports a different total, and is told the one that landed.
            var answered = await client.RecordCounterAsync(new RecordCounterRequest
            {
                VersionId = "grpc-keyed-release",
                Body = new CounterRecording { Key = "the bell", Total = 9, IdempotencyKey = counted },
            });

            Assert.Equal(4, answered.Data.Total);
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
    /// <summary>
    /// The index surface over the other transport: a build, the version it records, the search that answers from it,
    /// and the envelope of that answer resolved back to the version it names.
    /// </summary>
    [Fact]
    public async Task AVersionIsBuiltAndItsEnvelopesResolveOverGrpc() =>
        await WithClient(async client =>
        {
            var ingested = await client.IngestSourceAsync(new IngestSourceRequest
            {
                Body = new SourceIngest
                {
                    Path = "grpc-index/bell.txt",
                    MediaType = "text/plain",
                    Content = "The Bell rang twice at the north gate.",
                },
            });

            Assert.Equal("new", ingested.Data.Kind);

            var built = await client.BuildIndexVersionAsync(new BuildIndexVersionRequest
            {
                Body = new IndexBuild
                {
                    CollectionId = "col-grpc",
                    ShapeRef = "vendor@1",
                    PathPrefix = "grpc-index/",
                    Activate = true,
                },
            });

            Assert.True(built.Data.Active);
            Assert.StartsWith(IndexVersionIds.Prefix, built.Data.IndexVersionId, StringComparison.Ordinal);
            Assert.Single(built.Data.Manifest.SourceContentHashes);

            var live = await client.GetActiveIndexVersionAsync(
                new GetActiveIndexVersionRequest { CollectionId = "col-grpc" });

            Assert.Equal(built.Data.IndexVersionId, live.Data.IndexVersionId);

            var search = await client.SearchAsync(new SearchRequest
            {
                Body = new SearchQuery { Text = "the bell at the north gate", TopK = 5 },
            });

            Assert.Equal(built.Data.IndexVersionId, search.Data.Envelope.IndexVersion);

            var resolution = await client.ResolveEnvelopeAsync(new ResolveEnvelopeRequest
            {
                Body = new EnvelopeQuery
                {
                    IndexVersion = search.Data.Envelope.IndexVersion,
                    LedgerWatermark = search.Data.Envelope.LedgerWatermark,
                    Sources = { search.Data.Envelope.Sources },
                },
            });

            Assert.True(resolution.Data.Resolved);
            Assert.Equal(string.Empty, resolution.Data.Failure);
            Assert.Equal(built.Data.IndexVersionId, resolution.Data.Version.IndexVersionId);
        });

    /// <summary>
    /// A retry under one key is answered over gRPC too, and the key belongs to the command rather than to the transport:
    /// the two surfaces share one record of what was answered, so a retry over the other one is answered as well.
    /// </summary>
    [Fact]
    public async Task ARetriedClaimOverGrpcIsAnsweredRatherThanWrittenTwice()
    {
        string key = LedgerIds.New();

        await WithClient(async client =>
        {
            var proposal = Proposal("claim-grpc-keyed", """{"vendor_id":"g-keyed","status":"approved"}""");

            proposal.IdempotencyKey = key;

            var first = await client.ProposeClaimAsync(
                new ProposeClaimRequest { VersionId = "grpc-keyed", Body = proposal });

            Assert.Equal(ClaimStatus.Accepted, first.Data.Status);

            var second = await client.ProposeClaimAsync(
                new ProposeClaimRequest { VersionId = "grpc-keyed", Body = proposal });

            Assert.Equal(first.Data.Status, second.Data.Status);
            Assert.Equal(first.Data.Head, second.Data.Head);
        });

        // The same command, the same key, the other transport: still answered rather than written again.
        using var http = _factory.CreateClient();
        using var overJson = await http.PostAsJsonAsync(
            "/v1/versions/grpc-keyed/claims",
            new WireClaimProposal(
                "claim-grpc-keyed",
                WireClaimTypes.Fact,
                "vendor",
                """{"vendor_id":"g-keyed","status":"approved"}""",
                "the supplier is north",
                "tester",
                key),
            WireJson.Default.WireClaimProposal);

        var answered = (await overJson.Content.ReadFromJsonAsync(WireJson.Default.WireClaimOutcome))!;

        Assert.Equal(WireClaimStatus.Accepted, answered.Status);
        Assert.Equal(1, answered.Head);

        await WithClient(async client =>
        {
            var head = await client.GetHeadAsync(new GetHeadRequest { VersionId = "grpc-keyed" });

            Assert.Equal(1, head.Data.Head);
        });
    }

    /// <summary>The two list reads over the other transport, which is where their messages are generated from.</summary>
    [Fact]
    public async Task TheSourceAndVersionListsTravelOverGrpc() =>
        await WithClient(async client =>
        {
            await client.IngestSourceAsync(new IngestSourceRequest
            {
                Body = new SourceIngest
                {
                    Path = "grpc-list/a.txt",
                    MediaType = "text/plain",
                    Content = "The Bell rang twice at the north gate.",
                },
            });

            var built = await client.BuildIndexVersionAsync(new BuildIndexVersionRequest
            {
                Body = new IndexBuild
                {
                    CollectionId = "col-grpc-list",
                    ShapeRef = "vendor@1",
                    PathPrefix = "grpc-list/",
                    Activate = true,
                },
            });

            var sources = await client.ListSourcesAsync(new ListSourcesRequest { PathPrefix = "grpc-list/" });

            Assert.Equal("grpc-list/a.txt", Assert.Single(sources.Data.Sources).Path);

            var versions = await client.ListIndexVersionsAsync(
                new ListIndexVersionsRequest { CollectionId = "col-grpc-list" });

            Assert.Equal("col-grpc-list", versions.Data.CollectionId);
            Assert.Equal(built.Data.IndexVersionId, Assert.Single(versions.Data.Versions).IndexVersionId);
        });

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
