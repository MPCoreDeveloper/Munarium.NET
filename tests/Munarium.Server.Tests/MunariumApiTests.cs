namespace Munarium.Server.Tests;

using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Munarium.Access;
using Munarium.Context;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Sources;
using Munarium.Wire;

/// <summary>
/// The JSON/HTTP surface of the wire contract, exercised over the real application: a real kernel,
/// a real SharpCoreDB database, and the contract's own shapes.
/// </summary>
/// <remarks>
/// Every test writes to a version of its own, so the answers do not depend on the order xunit happens
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
    public async Task AVersionThatHasNotBeenWrittenToHasHeadZero()
    {
        var head = await GetAsync("/v1/versions/empty-version/head", WireJson.Default.WireVersionHead);

        Assert.Equal("empty-version", head.VersionId);
        Assert.Equal(0, head.Head);
    }

    [Fact]
    public async Task AnAcceptedClaimEchoesTheLineageTheKernelDerived()
    {
        var outcome = await ProposeAsync("version-accepted", "claim-accepted", Vendor("v-1"));

        Assert.Equal(WireClaimStatus.Accepted, outcome.Status);
        Assert.Equal("vendor@1|vendor_id=v-1", outcome.Lineage);
        Assert.Equal(1, outcome.Head);
        Assert.Equal(string.Empty, outcome.Gate);
    }

    [Fact]
    public async Task AClaimThatViolatesItsShapeIsRecordedAsDisputedWithTheReason()
    {
        var outcome = await ProposeAsync("version-disputed", "claim-disputed", """{"vendor_id":"v-2"}""");

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
            "version-unknown-shape",
            new WireClaimProposal(
                "claim-unknown", WireClaimTypes.Fact, "patent", """{"patent_id":"p-1"}""", "a claim", "tester"));

        Assert.Equal(WireClaimStatus.Disputed, outcome.Status);
        Assert.Equal("shape", outcome.Gate);
        Assert.Equal("patent@unregistered", outcome.Lineage);
    }

    [Fact]
    public async Task APinIsReproducibleAndALaterWriteDoesNotChangeIt()
    {
        await ProposeAsync("version-pin", "claim-pin-1", Vendor("v-7"));

        // Pin at the state the first claim produced, then supersede it and read both positions back.
        var beforeCorrection = await GetAsync("/v1/facts?as_of=0", WireJson.Default.WireFactSlice);
        var pin = beforeCorrection.AsOf;

        // The value legitimately changed, which the claim says in its type - an unnamed claim would be
        // refused as a ledger conflict, because a silent overwrite is what the gate exists to stop.
        var corrected = (await ProposeAsync(
            "version-pin", "claim-pin-2", Vendor("v-7", "pending"), WireClaimTypes.Update)).Head;

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

        var vendor = Assert.Single(shapes.Shapes, shape => shape.Name == VendorShape);

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

        // Which version answers depends on what the deployment has built, so the claim is that an answer carries one -
        // not that it is the version a freshly composed deployment starts with, which any build cuts over from.
        Assert.False(string.IsNullOrWhiteSpace(result.Envelope.IndexVersion));

        Assert.True(
            result.Envelope.LedgerWatermark >= 0,
            "an envelope names the ledger position its index reflected");
    }

    [Fact]
    public async Task AProposalMissingWhatTheContractRequiresIsRefusedAsAClientError()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/versions/version-invalid/claims",
            new WireClaimProposal(
                string.Empty, WireClaimTypes.Fact, VendorShape, Vendor("v-9"), "a claim", "tester"),
            WireJson.Default.WireClaimProposal);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.NotNull(problem);
        Assert.Equal("claim_id is required.", problem.Detail);
    }

    [Fact]
    public async Task TheJsonUsesTheFieldNamesTheContractDeclares()
    {
        await ProposeAsync("version-json", "claim-json", Vendor("v-json"));

        var response = await _client.GetAsync(new Uri("/v1/facts?as_of=0", UriKind.Relative));
        var json = await response.Content.ReadAsStringAsync();

        // The contract spells these this way, and no transport gets its own idea about that.
        Assert.Contains("\"as_of\"", json, StringComparison.Ordinal);
        Assert.Contains("\"digest\"", json, StringComparison.Ordinal);
        Assert.Contains("\"claim_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lineage\"", json, StringComparison.Ordinal);
        Assert.Contains("vendor@1|vendor_id=v-json", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key is spelled the contract's way on the request side too, which is what a caller who is not using this port's
    /// own types has to write: the contract names the field, and the transports do not each get an opinion about it.
    /// </summary>
    [Fact]
    public void AKeyIsSpelledTheContractsWayInARequestBody()
    {
        string key = LedgerIds.New();
        string field = $"\"idempotency_key\":\"{key}\"";

        Assert.Contains(
            field,
            JsonSerializer.Serialize(
                new WireCounterRecording("the bell", 4, 6, key), WireJson.Default.WireCounterRecording),
            StringComparison.Ordinal);

        Assert.Contains(
            field,
            JsonSerializer.Serialize(
                new WireAnchorLock("service", "api_version", "v2", string.Empty, string.Empty, key),
                WireJson.Default.WireAnchorLock),
            StringComparison.Ordinal);

        Assert.Contains(
            field,
            JsonSerializer.Serialize(
                new WirePromiseRegistration("audit-report", "deliverable", "an audit report", "api", "compliance", key),
                WireJson.Default.WirePromiseRegistration),
            StringComparison.Ordinal);

        Assert.Contains(
            field,
            JsonSerializer.Serialize(
                new WireVersionRequest("v-keyed", string.Empty, string.Empty, "keyed", "tester", key),
                WireJson.Default.WireVersionRequest),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionIsAClaimAndItsLineageIsRebuiltFromTheLedger()
    {
        await CreateVersionAsync("lineage-1", asOfDate: "2026-01-01", label: "the first one");
        await CreateVersionAsync("lineage-2", parent: "lineage-1", asOfDate: "2026-02-01");

        var lineage = await GetAsync("/v1/versions/lineage-2/lineage", WireJson.Default.WireVersionLineage);

        Assert.Equal(["lineage-1", "lineage-2"], lineage.Versions.Select(version => version.VersionId));

        var root = lineage.Versions[0];

        Assert.Equal(string.Empty, root.ParentVersionId);
        Assert.Equal("2026-01-01", root.AsOfDate);
        Assert.Equal("the first one", root.Label);

        // A version is a claim in the ledger, so its own creation is the whole of its stream.
        Assert.Equal(1, root.Head);
    }

    [Fact]
    public async Task ALineageStopsAtTheVersionThatWasAskedFor()
    {
        await CreateVersionAsync("lineage-3");
        await CreateVersionAsync("lineage-4", parent: "lineage-3");

        var lineage = await GetAsync("/v1/versions/lineage-3/lineage", WireJson.Default.WireVersionLineage);

        Assert.Equal(["lineage-3"], lineage.Versions.Select(version => version.VersionId));
    }

    [Fact]
    public async Task CreatingAVersionTwiceIsRefusedBecauseItsIdentityIsTaken()
    {
        await CreateVersionAsync("lineage-taken", label: "the first one");

        // The same identity with different content would silently replace a version, so it is refused. Only
        // an identical claim is treated as a retry, and a version's identity is a lineage like any other.
        var response = await PostVersionAsync(new WireVersionRequest(
            "lineage-taken", string.Empty, "2026-05-01", "a second one", "tester"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.NotNull(problem);

        // The refusal comes from the same gate as any other claim.
        Assert.Contains("ledger-conflict", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionThatDescendsFromNothingIsRefused()
    {
        var response = await PostVersionAsync(new WireVersionRequest(
            "lineage-orphan", "no-such-version", string.Empty, string.Empty, "tester"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownVersionHasNoLineage()
    {
        var response = await _client.GetAsync(new Uri("/v1/versions/no-such-version/lineage", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AVersionsIdentityIsGeneratedWhenNoneIsGiven()
    {
        var version = await CreateVersionAsync(string.Empty);

        // A ULID: 128 bits in Crockford base32, and it sorts by creation time.
        Assert.Equal(26, version.VersionId.Length);
        Assert.Equal(1, version.Head);
    }

    [Fact]
    public async Task AnUnnamedClaimOverAnExistingLineageIsRecordedAsDisputed()
    {
        await ProposeAsync("version-conflict", "claim-conflict-1", Vendor("v-conflict"));

        var outcome = await ProposeAsync("version-conflict", "claim-conflict-2", Vendor("v-conflict", "sanctioned"));

        Assert.Equal(WireClaimStatus.Disputed, outcome.Status);
        Assert.Equal("ledger-conflict", outcome.Gate);
        Assert.Contains("claim-conflict-1", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorrectionSaysWhyItSupersedesAndIsAccepted()
    {
        await ProposeAsync("version-correction", "claim-correction-1", Vendor("v-correct"));

        var outcome = await ProposeAsync(
            "version-correction",
            "claim-correction-2",
            Vendor("v-correct", "sanctioned"),
            WireClaimTypes.Correction);

        Assert.Equal(WireClaimStatus.Accepted, outcome.Status);
        Assert.Equal(WireClaimTypes.Correction, outcome.ClaimType);
        Assert.Equal("vendor@1|vendor_id=v-correct", outcome.Lineage);
    }

    [Fact]
    public async Task TheComposedContextIsAPureFunctionOfItsPin()
    {
        await ProposeAsync("version-context", "claim-context-1", Vendor("v-context"));

        var first = await ComposeAsync(Context("version-context"));
        var second = await ComposeAsync(Context("version-context"));

        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(Composer.EstimateTokens(first.Text.Length), first.EstimatedTokens);
        Assert.Contains("\"vendor_id\":\"v-context\"", first.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AComposedContextCarriesOneVersionAndNotTheOthers()
    {
        await ProposeAsync("version-context-a", "claim-context-a", Vendor("v-scope-a"));
        await ProposeAsync("version-context-b", "claim-context-b", Vendor("v-scope-b"));

        var composed = await ComposeAsync(Context("version-context-a"));

        Assert.Contains("v-scope-a", composed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("v-scope-b", composed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisputedClaimIsComposedUnderItsOwnSection()
    {
        await ProposeAsync("version-context-disputed", "claim-context-d", """{"vendor_id":"v-d"}""");

        var composed = await ComposeAsync(Context("version-context-disputed"));

        // A context that hid what the ledger refused would hide the one thing this system exists to keep
        // visible, so refusals travel with their reason.
        var disputed = Assert.Single(composed.Sections, section => section.Title == "Disputed");

        Assert.Contains("shape", disputed.Body, StringComparison.Ordinal);
        Assert.Contains("v-d", disputed.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTokenBudgetCapsTheFactsTheCompositionCarries()
    {
        await ProposeAsync("version-context-budget", "claim-budget-1", Vendor("v-budget-1"));
        await ProposeAsync("version-context-budget", "claim-budget-2", Vendor("v-budget-2"));

        var unbounded = await ComposeAsync(Context("version-context-budget"));
        var capped = await ComposeAsync(Context("version-context-budget", budgetTokens: 40));

        Assert.Contains("v-budget-1", capped.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("v-budget-2", capped.Text, StringComparison.Ordinal);
        Assert.True(capped.Text.Length < unbounded.Text.Length);

        // The budget bounds the facts. The marker that says facts were omitted is not counted against them,
        // because it is what tells a reader the context is partial - and it is shorter than the fact it
        // stands in for.
        Assert.Contains("more accepted fact(s) omitted", capped.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAsOfDateResolvesToAPinThroughTheVersions()
    {
        await CreateVersionAsync("version-dated-1", asOfDate: "2026-06-01");
        await ProposeAsync("version-dated-1", "claim-dated-1", Vendor("v-2026"));
        await CreateVersionAsync("version-dated-2", parent: "version-dated-1", asOfDate: "2026-07-01");

        var atTheFirst = await ComposeAsync(Context("version-dated-1", asOfDate: "2026-06-01"));
        var atTheSecond = await ComposeAsync(Context("version-dated-1", asOfDate: "2026-07-01"));

        // The date resolves to the position that version was created at, so at the first version's own
        // position the ledger holds its creation and nothing else - the claim comes after it. A date that
        // resolved to the present would make both read the same.
        Assert.Contains("starts a lineage", atTheFirst.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("v-2026", atTheFirst.Text, StringComparison.Ordinal);
        Assert.Contains("v-2026", atTheSecond.Text, StringComparison.Ordinal);
        Assert.True(atTheSecond.AsOf > atTheFirst.AsOf);
    }

    [Fact]
    public async Task AnAsOfDateNoVersionReachesIsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/context",
            new WireContextRequest(string.Empty, string.Empty, 0, "1999-01-01", 0, 0),
            WireJson.Default.WireContextRequest);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The candidate plane: a batch is judged as one unit and landed as one append, so the head moves by the
    /// number of claims rather than by the number of writes.
    /// </summary>
    [Fact]
    public async Task ABatchLandsAsOneAppend()
    {
        var outcome = await ProposeBatchAsync(
            "version-batch",
            Batch(("service", "api_version", "v2"), ("service", "owner_team", "platform")));

        Assert.Equal(2, outcome.Head);
        Assert.Equal(2, outcome.Claims.Count);
        Assert.All(outcome.Claims, claim => Assert.Equal(WireClaimStatus.Accepted, claim.Status));
        Assert.Equal(
            ["service.api_version", "service.owner_team"],
            outcome.Claims.Select(claim => claim.Lineage));

        // The claims are in the ledger, not merely in the response.
        var slice = await GetAsync("/v1/facts?version_id=version-batch", WireJson.Default.WireFactSlice);

        Assert.Equal(2, slice.Facts.Count);
    }

    /// <summary>
    /// A batch that names a position asks for that position: losing it is a contention rather than a quiet
    /// re-gate, because the caller asked to write at a head and not merely to write.
    /// </summary>
    [Fact]
    public async Task APinnedBatchThatLostTheHeadIsContended()
    {
        using var response = await _client.PostAsJsonAsync(
            "/v1/versions/version-batch-contended/claim-batches",
            new WireClaimBatchRequest(
                [Candidate("service", "api_version", "v2")],
                string.Empty,
                ExpectedHead: 99),
            WireJson.Default.WireClaimBatchRequest);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.ContendedWriteProblem, problem.Type);
        Assert.Equal(99, problem.ExpectedHead);
    }

    [Fact]
    public async Task ABatchMissingARequiredFieldNamesTheClaimThatMissedIt()
    {
        using var response = await _client.PostAsJsonAsync(
            "/v1/versions/version-batch-invalid/claim-batches",
            new WireClaimBatchRequest(
                [
                    Candidate("service", "api_version", "v2"),
                    new WireClaimCandidate(string.Empty, "service", "owner_team", string.Empty, string.Empty, string.Empty, string.Empty),
                ],
                string.Empty,
                ExpectedHead: 0),
            WireJson.Default.WireClaimBatchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        // The second claim is the one that missed a value, and nothing was recorded for either of them: a
        // request that was not understood is not a write.
        Assert.Equal(MunariumOperations.InvalidRequestProblem, problem.Type);
        Assert.Equal("claims[1]: value is required.", problem.Detail);

        var head = await GetAsync("/v1/versions/version-batch-invalid/head", WireJson.Default.WireVersionHead);

        Assert.Equal(0, head.Head);
    }

    /// <summary>
    /// The findings a write produced are readable per version, stamped with the position of the write - which is
    /// what makes a verdict citable rather than merely visible.
    /// </summary>
    [Fact]
    public async Task TheFindingsAWriteProducedAreReadablePerVersion()
    {
        await ProposeBatchAsync("version-findings", Batch(("service", "api_version", "v1")));
        var second = await ProposeBatchAsync("version-findings", Batch(("service", "api_version", "v2")));

        var findings = await GetAsync(
            "/v1/versions/version-findings/findings",
            WireJson.Default.WireFindingList);
        var blocked = Assert.Single(findings.Findings, stored => stored.Finding.Severity == WireSeverities.Block);

        Assert.Equal("service.api_version", blocked.Finding.ClaimKey);
        Assert.StartsWith("gate.", blocked.Finding.RuleId, StringComparison.Ordinal);
        Assert.Contains("claim_key", blocked.Finding.Detail, StringComparison.Ordinal);

        // The stamp is the position the write settled at in the version's own stream, which is the head that
        // append returned. A facts read answers on the global feed instead, so the two positions are citable
        // together through the version they belong to rather than by comparing them directly.
        Assert.Equal(second.Head, blocked.Sequence);

        var slice = await GetAsync("/v1/facts?version_id=version-findings", WireJson.Default.WireFactSlice);

        Assert.Single(slice.Facts, fact => fact.Status == WireClaimStatus.Disputed);
    }

    [Fact]
    public async Task TheFindingsReadSelectsBySeverityAndRule()
    {
        await ProposeBatchAsync("version-findings-filter", Batch(("service", "api_version", "v1")));
        await ProposeBatchAsync("version-findings-filter", Batch(("service", "api_version", "v2")));

        var warnings = await GetAsync(
            "/v1/versions/version-findings-filter/findings?severity=warn",
            WireJson.Default.WireFindingList);

        Assert.DoesNotContain(warnings.Findings, stored => stored.Finding.Severity == WireSeverities.Block);

        var gates = await GetAsync(
            "/v1/versions/version-findings-filter/findings?rule_prefix=gate.",
            WireJson.Default.WireFindingList);

        Assert.NotEmpty(gates.Findings);
        Assert.All(gates.Findings, stored => Assert.StartsWith("gate.", stored.Finding.RuleId, StringComparison.Ordinal));

        var none = await GetAsync(
            "/v1/versions/version-findings-filter/findings?rule_id=gate.no-such-rule",
            WireJson.Default.WireFindingList);

        Assert.Empty(none.Findings);
    }

    /// <summary>
    /// The audit read: one pin bounds facts, the digest ladder and the other three planes at once, and the
    /// instant comes from the snapshot's own identities rather than from a clock.
    /// </summary>
    [Fact]
    public async Task ASnapshotCarriesEveryPlaneAtOnePin()
    {
        await ProposeBatchAsync(
            "version-snapshot",
            Batch(("service", "api_version", "v2"), ("service", "owner_team", "platform")));

        var snapshot = await GetAsync("/v1/snapshots?version_id=version-snapshot", WireJson.Default.WireSnapshot);

        Assert.Equal("version-snapshot", snapshot.VersionId);
        Assert.True(snapshot.AsOfSequence > 0);
        Assert.Equal(
            ["service.api_version", "service.owner_team"],
            snapshot.Facts.Select(fact => $"{fact.Subject}.{fact.Key}"));
        Assert.All(snapshot.Facts, fact => Assert.Equal(WireClaimStatus.Accepted, fact.Status));
        Assert.All(snapshot.Facts, fact => Assert.Equal(WireProvenances.Witnessed, fact.Provenance));

        // The ladder is rebuilt from the pinned facts rather than looked up, so it is there to read.
        Assert.NotEmpty(snapshot.Digests);
        Assert.All(snapshot.Digests, rung => Assert.NotEmpty(rung.ContentHash));

        // Every identity in the snapshot is a ULID, so the snapshot carries when it was written.
        Assert.NotEmpty(snapshot.WrittenAt);
        Assert.NotEmpty(snapshot.WrittenOn);

        // The planes this version never wrote to are empty rather than absent.
        Assert.Empty(snapshot.Anchors);
        Assert.Empty(snapshot.Promises);
        Assert.Empty(snapshot.Counters);
        Assert.Empty(snapshot.Entities);
    }

    /// <summary>
    /// The limit and the scope filter are applied after resolution, which is the only order that answers "the
    /// newest fact of this scope": filtering first would let a superseded fact occupy the slot.
    /// </summary>
    [Fact]
    public async Task ASnapshotLimitsAndFiltersFactsAfterResolvingThem()
    {
        await ProposeBatchAsync(
            "version-snapshot-limit",
            Batch(("service", "api_version", "v2"), ("service", "owner_team", "platform")));

        var limited = await GetAsync(
            "/v1/snapshots?version_id=version-snapshot-limit&fact_limit=1",
            WireJson.Default.WireSnapshot);

        var newest = Assert.Single(limited.Facts);

        // The newer of the two facts is the one that survives the limit.
        Assert.Equal("service", newest.Subject);
        Assert.Equal("owner_team", newest.Key);

        var elsewhere = await GetAsync(
            "/v1/snapshots?version_id=version-snapshot-limit&scope=compliance",
            WireJson.Default.WireSnapshot);

        Assert.Empty(elsewhere.Facts);
    }

    /// <summary>
    /// A lock is a command, not a claim: it is recorded, it shows up in the plane, and a claim that contradicts it
    /// is refused by the anchor check rather than by the ledger conflict it would otherwise look like.
    /// </summary>
    [Fact]
    public async Task ALockedDetailCannotBeContradicted()
    {
        using var locked = await _client.PostAsJsonAsync(
            "/v1/versions/version-anchor/anchors",
            new WireAnchorLock("service", "api_version", "v2", "release", "{}"),
            WireJson.Default.WireAnchorLock);

        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);

        var anchor = (await locked.Content.ReadFromJsonAsync(WireJson.Default.WireAnchor))!;

        Assert.Equal("service.api_version", anchor.DetailKey);
        Assert.Equal("v2", anchor.LockedValue);
        Assert.Equal(WireAnchorStatuses.Locked, anchor.Status);

        var anchors = await GetAsync("/v1/versions/version-anchor/anchors", WireJson.Default.WireAnchorList);

        Assert.Equal("v2", Assert.Single(anchors.Anchors).LockedValue);

        var batch = await ProposeBatchAsync("version-anchor", Batch(("service", "api_version", "v3")));

        var claim = Assert.Single(batch.Claims);
        var finding = Assert.Single(batch.Findings, item => item.Severity == WireSeverities.Block);

        Assert.Equal(WireClaimStatus.Disputed, claim.Status);
        Assert.Equal(finding.RuleId, claim.Gate);
        Assert.Equal("service.api_version", finding.ClaimKey);
    }

    [Fact]
    public async Task ReleasingALockTwiceSaysSoTheSecondTime()
    {
        await _client.PostAsJsonAsync(
            "/v1/versions/version-anchor-release/anchors",
            new WireAnchorLock("service", "api_version", "v2", string.Empty, string.Empty),
            WireJson.Default.WireAnchorLock);

        using var released = await _client.PostAsync(
            "/v1/versions/version-anchor-release/anchors/service.api_version/release",
            content: null);

        Assert.Equal(HttpStatusCode.OK, released.StatusCode);

        var first = (await released.Content.ReadFromJsonAsync(WireJson.Default.WireAnchorRelease))!;

        Assert.True(first.Released);

        var anchors = await GetAsync("/v1/versions/version-anchor-release/anchors", WireJson.Default.WireAnchorList);

        Assert.Empty(anchors.Anchors);

        using var again = await _client.PostAsync(
            "/v1/versions/version-anchor-release/anchors/service.api_version/release",
            content: null);

        var second = (await again.Content.ReadFromJsonAsync(WireJson.Default.WireAnchorRelease))!;

        Assert.False(second.Released);
    }

    /// <summary>A detail key that names no property could never have been locked, so the release is refused.</summary>
    [Fact]
    public async Task ReleasingAKeyThatNamesNoPropertyIsRefused()
    {
        using var response = await _client.PostAsync(
            "/v1/versions/version-anchor-key/anchors/service/release",
            content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task APromiseIsOpenUntilItIsFulfilled()
    {
        using var opened = await _client.PostAsJsonAsync(
            "/v1/versions/version-promise/promises",
            new WirePromiseRegistration(
                "audit-report", "deliverable", "an audit report for the release", "release", "compliance"),
            WireJson.Default.WirePromiseRegistration);

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        var promise = (await opened.Content.ReadFromJsonAsync(WireJson.Default.WirePromise))!;

        Assert.Equal(WirePromiseStatuses.Open, promise.Status);
        Assert.Equal("compliance", promise.DueScope);

        var open = await GetAsync("/v1/versions/version-promise/promises", WireJson.Default.WirePromiseList);

        Assert.Equal(WirePromiseStatuses.Open, Assert.Single(open.Promises).Status);

        using var fulfilled = await _client.PostAsync(
            "/v1/versions/version-promise/promises/audit-report/fulfill",
            content: null);

        var done = (await fulfilled.Content.ReadFromJsonAsync(WireJson.Default.WirePromiseFulfilment))!;

        Assert.True(done.Fulfilled);

        var after = await GetAsync(
            "/v1/versions/version-promise/promises?status=fulfilled",
            WireJson.Default.WirePromiseList);

        Assert.Equal(WirePromiseStatuses.Fulfilled, Assert.Single(after.Promises).Status);

        // Fulfilling it again settles nothing, and says so rather than failing.
        using var again = await _client.PostAsync(
            "/v1/versions/version-promise/promises/audit-report/fulfill",
            content: null);

        var nothing = (await again.Content.ReadFromJsonAsync(WireJson.Default.WirePromiseFulfilment))!;

        Assert.False(nothing.Fulfilled);
    }

    /// <summary>
    /// The overdue view is computed over the full pinned slice, before the status filter narrows it: a finding a
    /// filter could hide would be a finding nobody sees.
    /// </summary>
    [Fact]
    public async Task TheOverdueViewSurvivesTheStatusFilter()
    {
        await _client.PostAsJsonAsync(
            "/v1/versions/version-promise-overdue/promises",
            new WirePromiseRegistration("audit-report", "deliverable", "an audit report", "release", "compliance"),
            WireJson.Default.WirePromiseRegistration);

        var overdue = await GetAsync(
            "/v1/versions/version-promise-overdue/promises?overdue_scope=compliance&final=true",
            WireJson.Default.WirePromiseList);

        Assert.Single(overdue.Promises);

        var finding = Assert.Single(overdue.Findings);

        Assert.Equal("gate.promise-unfulfilled", finding.RuleId);
        Assert.Equal(WireSeverities.Warn, finding.Severity);
        Assert.Contains("audit-report", finding.Message, StringComparison.Ordinal);

        var filtered = await GetAsync(
            "/v1/versions/version-promise-overdue/promises?overdue_scope=compliance&status=fulfilled",
            WireJson.Default.WirePromiseList);

        Assert.Empty(filtered.Promises);
        Assert.Single(filtered.Findings);
    }

    /// <summary>
    /// A counter is an absolute total, and the answer tells the writer where it stands rather than only that it
    /// overspent - which is the point of keeping a count at all.
    /// </summary>
    [Fact]
    public async Task ACounterIsRecordedAndItsDirectivesTellAWriterWhereItStands()
    {
        using var first = await _client.PostAsJsonAsync(
            "/v1/versions/version-counter/counters",
            new WireCounterRecording("the bell", 4, 6),
            WireJson.Default.WireCounterRecording);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var recorded = (await first.Content.ReadFromJsonAsync(WireJson.Default.WireCounter))!;

        Assert.Equal(4, recorded.Total);
        Assert.Equal(6, recorded.Budget);
        Assert.False(recorded.OverBudget);

        // Recording again replaces the total rather than adding to it: the total is absolute.
        using var second = await _client.PostAsJsonAsync(
            "/v1/versions/version-counter/counters",
            new WireCounterRecording("the bell", 7, 6),
            WireJson.Default.WireCounterRecording);

        var over = (await second.Content.ReadFromJsonAsync(WireJson.Default.WireCounter))!;

        Assert.True(over.OverBudget);

        var counters = await GetAsync("/v1/versions/version-counter/counters", WireJson.Default.WireCounterList);

        Assert.Equal(7, Assert.Single(counters.Counters).Total);
        Assert.Contains("the bell: used 7/6", counters.Directives, StringComparison.Ordinal);
        Assert.Contains("AVOID: 'the bell'", counters.Directives, StringComparison.Ordinal);
    }

    /// <summary>The contract counts in int64 and the plane in ulong, so a negative count is refused rather than wrapped.</summary>
    [Fact]
    public async Task ACountThatCannotBeUnderstoodIsRefused()
    {
        using var negative = await _client.PostAsJsonAsync(
            "/v1/versions/version-counter-invalid/counters",
            new WireCounterRecording("the bell", -3, 0),
            WireJson.Default.WireCounterRecording);

        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

        using var unnamed = await _client.PostAsJsonAsync(
            "/v1/versions/version-counter-invalid/counters",
            new WireCounterRecording(string.Empty, 3, 0),
            WireJson.Default.WireCounterRecording);

        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);

        var counters = await GetAsync(
            "/v1/versions/version-counter-invalid/counters",
            WireJson.Default.WireCounterList);

        Assert.Empty(counters.Counters);
        Assert.Empty(counters.Directives);
    }

    private static string Vendor(string vendorId, string status = "approved") =>
        $$"""{"vendor_id":"{{vendorId}}","status":"{{status}}"}""";

    private async Task<WireClaimOutcome> ProposeAsync(
        string version,
        string claimId,
        string body,
        string claimType = WireClaimTypes.Fact) =>
        await ProposeAsync(
            version,
            new WireClaimProposal(claimId, claimType, VendorShape, body, "the supplier is north", "tester"));

    private async Task<WireClaimOutcome> ProposeAsync(string version, WireClaimProposal proposal)
    {
        var response = await _client.PostAsJsonAsync(
            $"/v1/versions/{version}/claims",
            proposal,
            WireJson.Default.WireClaimProposal);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync(WireJson.Default.WireClaimOutcome))!;
    }

    private static WireClaimBatchRequest Batch(
        params (string Subject, string Key, string Value)[] claims) =>
        new([.. claims.Select(claim => Candidate(claim.Subject, claim.Key, claim.Value))], string.Empty, ExpectedHead: 0);

    private static WireClaimCandidate Candidate(string subject, string key, string value) =>
        new(string.Empty, subject, key, value, string.Empty, string.Empty, string.Empty);

    private async Task<WireClaimBatchOutcome> ProposeBatchAsync(string version, WireClaimBatchRequest request)
    {
        var response = await _client.PostAsJsonAsync(
            $"/v1/versions/{version}/claim-batches",
            request,
            WireJson.Default.WireClaimBatchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync(WireJson.Default.WireClaimBatchOutcome))!;
    }

    private static WireContextRequest Context(string version, string asOfDate = "", int budgetTokens = 0) =>
        new(version, string.Empty, 0, asOfDate, budgetTokens, 0);

    private async Task<HttpResponseMessage> PostVersionAsync(WireVersionRequest request) =>
        await _client.PostAsJsonAsync("/v1/versions", request, WireJson.Default.WireVersionRequest);

    private async Task<WireVersion> CreateVersionAsync(
        string versionId,
        string parent = "",
        string asOfDate = "",
        string label = "")
    {
        var response = await PostVersionAsync(
            new WireVersionRequest(versionId, parent, asOfDate, label, "tester"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync(WireJson.Default.WireVersion))!;
    }

    private async Task<WireComposedContext> ComposeAsync(WireContextRequest request)
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/context",
            request,
            WireJson.Default.WireContextRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync(WireJson.Default.WireComposedContext))!;
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type)
    {
        var response = await _client.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync(type))!;
    }

    /// <summary>
    /// A document goes in and comes back out of retrieval, with the citation resolving to the path and hash it was
    /// stored under. This is the whole chain in one test: source, row, chunks, index, envelope, watermark.
    /// </summary>
    [Fact]
    public async Task AnIngestedDocumentIsStoredAndFoundBySearch()
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest(
                "docs/bell.txt",
                "text/plain",
                "The Bell rang twice at the north gate.\n\nThe north gate is the loading bay.",
                string.Empty),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var ingested = (await put.Content.ReadFromJsonAsync(WireJson.Default.WireIngestedSource))!;

        Assert.Equal("new", ingested.Kind);
        Assert.True(ingested.ChunksIndexed > 0, "the document should have been chunked and indexed");
        Assert.Equal(SourceKey.Id(MunariumKernel.Tenant, "docs/bell.txt"), ingested.SourceId);
        Assert.Equal("sharpcoredb", ingested.BackendId);
        Assert.NotEmpty(ingested.ContentHash);
        Assert.NotEmpty(ingested.IngestedAt ?? string.Empty);
        Assert.NotEmpty(ingested.IndexVersion);

        // The row is readable by identity, and it carries where the bytes went - never the bytes.
        var info = await GetAsync($"/v1/sources/{ingested.SourceId}", WireJson.Default.WireSourceInfo);

        Assert.Equal("docs/bell.txt", info.Path);
        Assert.Equal(ingested.ContentHash, info.ContentHash);
        Assert.Equal(ingested.Bytes, info.Bytes);

        // How extraction went is served beside the hash: a document that contributed nothing says so, rather than looking
        // like one nobody ingested.
        Assert.Equal("ok", info.ExtractionStatus);
        Assert.Equal("text", info.ExtractionMethod);

        // And retrieval finds it, citing the source and the bytes it held when indexed.
        using var search = await _client.PostAsJsonAsync(
            "/v1/search",
            new WireSearchQuery("the bell at the north gate", 5),
            WireJson.Default.WireSearchQuery);

        var result = (await search.Content.ReadFromJsonAsync(WireJson.Default.WireSearchResult))!;
        var chunk = Assert.Single(result.Chunks, found => found.Source.SourcePath == "docs/bell.txt");

        Assert.Equal(ingested.SourceId, chunk.Source.SourceId);
        Assert.Equal(ingested.ContentHash, chunk.Source.ContentHash);
        Assert.StartsWith(ingested.SourceId, chunk.Source.ChunkId, StringComparison.Ordinal);
        Assert.Equal(ingested.IndexVersion, result.Envelope.IndexVersion);
    }

    /// <summary>
    /// A re-put of the same bytes is not a second ingest: nothing is written and nothing is indexed twice, because a
    /// document indexed twice would answer twice and displace other documents by existing.
    /// </summary>
    [Fact]
    public async Task ReIngestingTheSameDocumentChangesNothing()
    {
        var request = new WireSourceIngest(
            "docs/steady.txt",
            "text/plain",
            "A document that does not move.",
            string.Empty);

        using var first = await _client.PutAsJsonAsync(
            "/v1/sources", request, WireJson.Default.WireSourceIngest);

        var ingested = (await first.Content.ReadFromJsonAsync(WireJson.Default.WireIngestedSource))!;
        Assert.Equal("new", ingested.Kind);

        using var second = await _client.PutAsJsonAsync(
            "/v1/sources", request, WireJson.Default.WireSourceIngest);

        var again = (await second.Content.ReadFromJsonAsync(WireJson.Default.WireIngestedSource))!;

        Assert.Equal("unchanged", again.Kind);
        Assert.Equal(0, again.ChunksIndexed);
        Assert.Equal(ingested.ContentHash, again.ContentHash);
    }

    [Fact]
    public async Task AChangedDocumentAtTheSamePathIsOneSourceReplaced()
    {
        using var first = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest("docs/moved.txt", "text/plain", "The Bell rang twice.", string.Empty),
            WireJson.Default.WireSourceIngest);

        var before = (await first.Content.ReadFromJsonAsync(WireJson.Default.WireIngestedSource))!;

        using var second = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest("docs/moved.txt", "text/plain", "The Bell rang three times.", string.Empty),
            WireJson.Default.WireSourceIngest);

        var after = (await second.Content.ReadFromJsonAsync(WireJson.Default.WireIngestedSource))!;

        Assert.Equal("replaced", after.Kind);
        Assert.Equal(before.SourceId, after.SourceId);
        Assert.NotEqual(before.ContentHash, after.ContentHash);
    }

    /// <summary>
    /// A document this port cannot read is refused by name before anything is stored: an unreadable document that was
    /// stored anyway would be a source that can never be retrieved.
    /// </summary>
    /// <summary>A capability is minted, and it verifies against the deployment secret with the claims that were asked for.</summary>
    /// <remarks>
    /// This is the whole plane in one test: what the route issues is a signed capability this process can verify, which is
    /// exactly what a data plane does with it - and it reads as a principal narrower than the deployment one.
    /// </remarks>
    [Fact]
    public async Task ACapabilityIsIssuedAndVerifies()
    {
        using var post = await _client.PostAsJsonAsync(
            "/v1/access-tokens",
            new WireAccessTokenRequest("tyler@example.com", 3, ["north"], [AccessScope.Query], ["vendor-memory"], 60),
            WireJson.Default.WireAccessTokenRequest);

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var issued = (await post.Content.ReadFromJsonAsync(WireJson.Default.WireAccessToken))!;

        Assert.NotEmpty(issued.TokenId);

        var outcome = AccessTokens.Verify(MunariumKernel.AccessSecret, issued.Token, DateTimeOffset.UtcNow);

        Assert.True(outcome is AccessClaims, $"the capability was refused: {outcome}");

        if (outcome is not AccessClaims verified)
        {
            throw new InvalidOperationException($"the capability was refused: {outcome}");
        }

        Assert.Equal("tyler@example.com", verified.Subject);
        Assert.Equal(3, verified.Level);
        Assert.Equal(["north"], verified.Compartments);
        Assert.Equal([AccessScope.Query], verified.Scopes);
        Assert.Equal(issued.TokenId, verified.TokenId);
        Assert.Equal(issued.ExpiresAt, verified.ExpiresAt);

        // It reads as the principal a plane resolves as, and it is not the deployment one.
        var principal = verified.ToPrincipal();

        Assert.Equal("tyler@example.com", principal.Uid);
        Assert.Equal(3, principal.Level);
        Assert.False(principal.AllCompartments);
    }

    /// <summary>What cannot be issued is refused by naming the rule, which is the difference between fixing and guessing.</summary>
    /// <param name="subject">The subject asked for.</param>
    /// <param name="scopes">The scopes asked for, or empty.</param>
    [Theory]
    [InlineData("", "query")]
    [InlineData("tyler@example.com", "")]
    [InlineData("tyler@example.com", "admin")]
    public async Task ACapabilityThatCannotBeIssuedIsRefused(string subject, string scopes)
    {
        using var post = await _client.PostAsJsonAsync(
            "/v1/access-tokens",
            new WireAccessTokenRequest(
                subject,
                1,
                [],
                string.IsNullOrEmpty(scopes) ? [] : [scopes]),
            WireJson.Default.WireAccessTokenRequest);

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);

        var problem = (await post.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.InvalidRequestProblem, problem.Type);
    }
    /// <summary>Ingestion asks for the ingest scope, and a capability without it is refused before anything is stored.</summary>
    [Fact]
    public async Task IngestionAsksForTheIngestScope()
    {
        var queryOnly = await Minted([AccessScope.Query]);

        using var refused = await PutWith(queryOnly, "docs/denied.txt");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        var mayUpload = await Minted([AccessScope.Ingest]);

        using var accepted = await PutWith(mayUpload, "docs/allowed.txt");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    /// <summary>Mints a capability for a test.</summary>
    /// <param name="scopes">The scopes it carries.</param>
    /// <returns>The token itself.</returns>
    private async Task<string> Minted(IReadOnlyList<string> scopes)
    {
        using var minted = await _client.PostAsJsonAsync(
            "/v1/access-tokens",
            new WireAccessTokenRequest("tyler@example.com", 9, [], scopes),
            WireJson.Default.WireAccessTokenRequest);

        var issued = (await minted.Content.ReadFromJsonAsync(WireJson.Default.WireAccessToken))!;

        return issued.Token;
    }

    /// <summary>Offers a document with a capability presented.</summary>
    /// <param name="token">The capability.</param>
    /// <param name="path">The path to ingest.</param>
    /// <returns>The answer.</returns>
    private async Task<HttpResponseMessage> PutWith(string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/v1/sources")
        {
            Content = JsonContent.Create(
                new WireSourceIngest(path, "text/plain", "The Bell rang twice.", string.Empty),
                WireJson.Default.WireSourceIngest),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await _client.SendAsync(request);
    }
    public async Task ADocumentWithNoExtractorIsRefusedAndNothingIsStored()
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest("docs/plate.tiff", "image/tiff", "II*\u0004", string.Empty),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, put.StatusCode);

        var problem = (await put.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.UnsupportedMediaTypeProblem, problem.Type);
        Assert.Contains("image/tiff", problem.Detail, StringComparison.Ordinal);

        using var missing = await _client.GetAsync(
            new Uri($"/v1/sources/{SourceKey.Id(MunariumKernel.Tenant, "docs/plate.tiff")}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// A document that is not text travels as bytes, and it is read: a DOCX out of its own XML.
    /// </summary>
    /// <remarks>
    /// This is what the bytes form is for, and it is the whole chain again - the row is written, extraction turns the
    /// bytes into text, and the text is chunked and indexed - so a binary document is retrievable like any other.
    /// </remarks>
    [Fact]
    public async Task ADocxArrivesAsBytesAndIsIndexed()
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            AsBytes("docs/policy.docx", DocxMediaType, Docx("Vacation Policy", "Employees accrue 15 days.")),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var ingested = (await put.Content.ReadFromJsonAsync(WireJson.Default.WireIngestedSource))!;

        Assert.Equal("new", ingested.Kind);
        Assert.True(ingested.ChunksIndexed > 0, "a DOCX's paragraphs should have been chunked and indexed");
        Assert.Equal(DocxMediaType, ingested.MediaType);
    }

    /// <summary>A PDF's text layer is read through the same seam, which is what makes a PDF retrievable at all.</summary>
    [Fact]
    public async Task APdfArrivesAsBytesAndIsIndexed()
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            AsBytes("docs/settlement.pdf", "application/pdf", Pdf("The quarterly settlement was approved on 14 March")),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var ingested = (await put.Content.ReadFromJsonAsync(WireJson.Default.WireIngestedSource))!;

        Assert.True(ingested.ChunksIndexed > 0, "a PDF with a text layer should have been chunked and indexed");
    }

    /// <summary>
    /// A scan has no text layer, so it is stored and indexes nothing: not a failure, but the honest answer - and the row
    /// still accounts for the bytes.
    /// </summary>
    [Fact]
    public async Task AScanIsStoredAndIndexesNothing()
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            AsBytes("docs/scan.pdf", "application/pdf", Pdf(string.Empty)),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var ingested = (await put.Content.ReadFromJsonAsync(WireJson.Default.WireIngestedSource))!;

        Assert.Equal(0, ingested.ChunksIndexed);

        var info = await GetAsync($"/v1/sources/{ingested.SourceId}", WireJson.Default.WireSourceInfo);

        Assert.Equal("docs/scan.pdf", info.Path);
        Assert.True(info.Bytes > 0, "the bytes are stored even when nothing is indexed");
    }

    /// <summary>One document, one way to carry it: a body with both forms, or with neither, is malformed.</summary>
    /// <param name="both">Whether the body carries both forms rather than neither.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADocumentCarriedBothWaysOrNeitherIsRefused(bool both)
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest(
                "docs/ambiguous.txt",
                "text/plain",
                both ? "The Bell rang twice." : null,
                string.Empty,
                both ? Convert.ToBase64String("The Bell rang twice."u8.ToArray()) : null),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        var problem = (await put.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.InvalidRequestProblem, problem.Type);
        Assert.Contains("content", problem.Detail, StringComparison.Ordinal);
    }

    /// <summary>Base64 that is not base64 is a malformed request rather than a document that arrived.</summary>
    [Fact]
    public async Task Base64ThatIsNotBase64IsRefused()
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest("docs/broken.docx", DocxMediaType, null, string.Empty, "not base64 at all!!"),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        var problem = (await put.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Contains("base64", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclaredHashThatDoesNotMatchIsRefused()
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest("docs/declared.txt", "text/plain", "The Bell rang twice.", "sha256:0000"),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);

        var problem = (await put.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.ContentHashMismatchProblem, problem.Type);
        Assert.Contains("sha256:0000", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathASourceMayNotHoldIsRefused()
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest("../escape.txt", "text/plain", "The Bell rang twice.", string.Empty),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    /// <summary>
    /// The whole chain, end to end: documents go in, a version is built over them, the build cuts the collection over,
    /// a search answers from it, and the envelope that search carries resolves against the version it names.
    /// </summary>
    [Fact]
    public async Task ABuiltVersionServesAndTheEnvelopeOfAnAnswerFromItResolves()
    {
        // A claim first, so the ledger has a position for the build to reflect: a version's watermark is where an
        // answer's provenance ends, and on an empty ledger that is zero.
        await ProposeAsync("version-index-build", "claim-index-build", """{"vendor_id":"v-idx"}""");

        await IngestAsync("idx-docs/bell.txt", "The Bell rang twice at the north gate.");
        await IngestAsync("idx-docs/other.txt", "A second document about the south gate.");

        using var built = await _client.PostAsJsonAsync(
            "/v1/indexes",
            new WireIndexBuild("col-idx", "Indexed documents", "vendor@1", "idx-docs/", Activate: true),
            WireJson.Default.WireIndexBuild);

        Assert.Equal(HttpStatusCode.OK, built.StatusCode);

        var version = (await built.Content.ReadFromJsonAsync(WireJson.Default.WireIndexVersion))!;

        Assert.StartsWith(IndexVersionIds.Prefix, version.IndexVersionId, StringComparison.Ordinal);
        Assert.True(version.Active);
        Assert.True(version.Watermark > 0, "a build reflects the ledger position it was made at");
        Assert.Equal("chunk@1", version.Manifest.Chunker);
        Assert.Equal(Munarium.Text.TextExtractor.Version(), version.Manifest.Extractors);
        Assert.Contains(DeterministicEmbeddingProvider.ModelName, version.Manifest.Embedder, StringComparison.Ordinal);
        Assert.Equal(2, version.Manifest.SourceContentHashes.Count);

        // The read routes agree with the build.
        var live = await GetAsync("/v1/indexes/active?collection_id=col-idx", WireJson.Default.WireIndexVersion);
        var byId = await GetAsync($"/v1/indexes/{version.IndexVersionId}", WireJson.Default.WireIndexVersion);

        Assert.Equal(version.IndexVersionId, live.IndexVersionId);
        Assert.Equal(version.IndexVersionId, byId.IndexVersionId);
        Assert.NotNull(version.ActivatedAt);

        // A search now answers from the built version, and its envelope resolves back to it.
        using var search = await _client.PostAsJsonAsync(
            "/v1/search",
            new WireSearchQuery("the bell at the north gate", 5),
            WireJson.Default.WireSearchQuery);

        var result = (await search.Content.ReadFromJsonAsync(WireJson.Default.WireSearchResult))!;

        Assert.Equal(version.IndexVersionId, result.Envelope.IndexVersion);
        Assert.Contains(result.Chunks, chunk => chunk.Source.SourcePath == "idx-docs/bell.txt");

        using var resolved = await _client.PostAsJsonAsync(
            "/v1/indexes/resolve",
            new WireEnvelopeQuery(result.Envelope.IndexVersion, result.Envelope.LedgerWatermark, result.Envelope.Sources),
            WireJson.Default.WireEnvelopeQuery);

        var resolution = (await resolved.Content.ReadFromJsonAsync(WireJson.Default.WireEnvelopeResolution))!;

        Assert.True(resolution.Resolved);
        Assert.True(string.IsNullOrEmpty(resolution.Failure), resolution.Failure);
        Assert.Empty(resolution.UnrecordedContentHashes);
        Assert.Equal(version.IndexVersionId, resolution.Version?.IndexVersionId);
        Assert.Equal(version.Watermark, resolution.Version?.Watermark);
    }

    private async Task IngestAsync(string path, string content)
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest(path, "text/plain", content, string.Empty),
            WireJson.Default.WireSourceIngest);

        put.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// What was ingested is readable as a set, and a prefix selects what a collection would bind - which is how an
    /// operator checks the binding before building over it.
    /// </summary>
    [Fact]
    public async Task TheSourcesThatWereIngestedAreListed()
    {
        await IngestAsync("idx-list/a.txt", "The first document.");
        await IngestAsync("idx-list/nested/b.txt", "The second document.");

        var bound = await GetAsync("/v1/sources?path_prefix=idx-list/", WireJson.Default.WireSourceList);

        Assert.Equal(["idx-list/a.txt", "idx-list/nested/b.txt"], bound.Sources.Select(source => source.Path));
        Assert.All(bound.Sources, source => Assert.True(source.Bytes > 0));

        var elsewhere = await GetAsync("/v1/sources?path_prefix=nothing-here/", WireJson.Default.WireSourceList);

        Assert.Empty(elsewhere.Sources);
    }

    /// <summary>
    /// A collection's versions are readable, so an operator can see what to cut over to - including the version that
    /// stopped serving, because cutting back to one is a cutover rather than a restore.
    /// </summary>
    [Fact]
    public async Task ACollectionsVersionsAreListed()
    {
        await IngestAsync("idx-versions/a.txt", "The Bell rang twice at the north gate.");

        var first = await BuiltAsync(await _client.PostAsJsonAsync(
            "/v1/indexes",
            new WireIndexBuild("col-versions", string.Empty, "vendor@1", "idx-versions/", Activate: true),
            WireJson.Default.WireIndexBuild));

        // A second document changes the corpus, so the rebuild is a version of its own.
        await IngestAsync("idx-versions/b.txt", "A second document about the south gate.");

        var second = await BuiltAsync(await _client.PostAsJsonAsync(
            "/v1/indexes",
            new WireIndexBuild("col-versions", string.Empty, "vendor@1", "idx-versions/", Activate: true),
            WireJson.Default.WireIndexBuild));

        Assert.NotEqual(first.IndexVersionId, second.IndexVersionId);

        var versions = await GetAsync(
            "/v1/indexes?collection_id=col-versions", WireJson.Default.WireIndexVersionList);

        Assert.Equal("col-versions", versions.CollectionId);
        Assert.Equal(2, versions.Versions.Count);
        Assert.Equal(second.IndexVersionId, versions.Versions[0].IndexVersionId);
        Assert.True(versions.Versions[0].Active);
        Assert.Equal(first.IndexVersionId, versions.Versions[1].IndexVersionId);
        Assert.True(versions.Versions[1].Superseded);

        var unknown = await GetAsync(
            "/v1/indexes?collection_id=col-nothing", WireJson.Default.WireIndexVersionList);

        Assert.Empty(unknown.Versions);
    }

    private static async Task<WireIndexVersion> BuiltAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync(WireJson.Default.WireIndexVersion))!;

    [Fact]
    public async Task ASourceThatWasNeverIngestedHasNoRow()
    {
        using var response = await _client.GetAsync(new Uri("/v1/sources/src-0000000000000000", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.UnknownSourceProblem, problem.Type);
    }

    /// <summary>
    /// An envelope that cites bytes the version never held does not resolve: a version records what it indexed, so an
    /// answer citing something outside that set cannot have come from it.
    /// </summary>
    [Fact]
    public async Task AnEnvelopeCitingBytesOutsideTheVersionDoesNotResolve()
    {
        await IngestAsync("idx-foreign/a.txt", "A document about the west gate.");

        using var built = await _client.PostAsJsonAsync(
            "/v1/indexes",
            new WireIndexBuild("col-foreign", string.Empty, "vendor@1", "idx-foreign/", Activate: false),
            WireJson.Default.WireIndexBuild);

        var version = (await built.Content.ReadFromJsonAsync(WireJson.Default.WireIndexVersion))!;

        using var resolved = await _client.PostAsJsonAsync(
            "/v1/indexes/resolve",
            new WireEnvelopeQuery(
                version.IndexVersionId,
                0,
                [
                    new WireSourceReference(
                        "chunk-1", "src-0000000000000000", "docs/elsewhere.txt", "sha256:elsewhere", 0),
                ]),
            WireJson.Default.WireEnvelopeQuery);

        var resolution = (await resolved.Content.ReadFromJsonAsync(WireJson.Default.WireEnvelopeResolution))!;

        Assert.False(resolution.Resolved);
        Assert.Contains("sha256:elsewhere", resolution.UnrecordedContentHashes);
        Assert.False(string.IsNullOrEmpty(resolution.Failure));
    }

    /// <summary>
    /// The version a deployment starts by serving was composed rather than built, so nobody recorded what it holds - and
    /// the resolution says exactly that rather than pretending it is fine.
    /// </summary>
    [Fact]
    public async Task AnEnvelopeFromAnIndexNobodyBuiltDoesNotResolve()
    {
        using var resolved = await _client.PostAsJsonAsync(
            "/v1/indexes/resolve",
            new WireEnvelopeQuery(MunariumKernel.InitialIndexVersion, 0, []),
            WireJson.Default.WireEnvelopeQuery);

        var resolution = (await resolved.Content.ReadFromJsonAsync(WireJson.Default.WireEnvelopeResolution))!;

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Version);
        Assert.Contains("cannot be resolved", resolution.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildWithNothingBoundIsRefused()
    {
        using var built = await _client.PostAsJsonAsync(
            "/v1/indexes",
            new WireIndexBuild("col-empty", string.Empty, "vendor@1", "nothing-here/", Activate: false),
            WireJson.Default.WireIndexBuild);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, built.StatusCode);

        var problem = (await built.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.IndexBuildRefusedProblem, problem.Type);
        Assert.Contains("nothing-here/", problem.Detail, StringComparison.Ordinal);
    }

    /// <summary>A version can only be activated into its own collection, and only one this process built.</summary>
    [Fact]
    public async Task AVersionCannotBeActivatedIntoAnotherCollectionOrFromElsewhere()
    {
        await IngestAsync("idx-scope/a.txt", "A document about the east gate.");

        using var built = await _client.PostAsJsonAsync(
            "/v1/indexes",
            new WireIndexBuild("col-scope", string.Empty, "vendor@1", "idx-scope/", Activate: false),
            WireJson.Default.WireIndexBuild);

        var version = (await built.Content.ReadFromJsonAsync(WireJson.Default.WireIndexVersion))!;

        using var elsewhere = await _client.PostAsJsonAsync(
            $"/v1/indexes/{version.IndexVersionId}/activate",
            new WireIndexActivation("col-other"),
            WireJson.Default.WireIndexActivation);

        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);

        using var unknown = await _client.PostAsJsonAsync(
            "/v1/indexes/idx-0000000000000000/activate",
            new WireIndexActivation("col-scope"),
            WireJson.Default.WireIndexActivation);

        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);

        var problem = (await unknown.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.IndexNotBuiltHereProblem, problem.Type);

        // Activating it into its own collection works, and the route answers with it as live.
        using var activated = await _client.PostAsJsonAsync(
            $"/v1/indexes/{version.IndexVersionId}/activate",
            new WireIndexActivation("col-scope"),
            WireJson.Default.WireIndexActivation);

        var live = (await activated.Content.ReadFromJsonAsync(WireJson.Default.WireIndexVersion))!;

        Assert.True(live.Active);
        Assert.Null(live.DeactivatedAt);
        Assert.Equal(version.IndexVersionId, live.IndexVersionId);
    }

    /// <summary>
    /// A retry under the same key is answered rather than done again: the second attempt writes nothing, and the caller
    /// is told exactly what it was told the first time - which is the whole point of a key.
    /// </summary>
    [Fact]
    public async Task ARetriedClaimUnderOneKeyIsAnsweredRatherThanWrittenTwice()
    {
        var proposal = new WireClaimProposal(
            "claim-keyed",
            WireClaimTypes.Fact,
            VendorShape,
            Vendor("v-keyed"),
            "the supplier is north",
            "tester",
            LedgerIds.New());

        var first = await ProposeAsync("version-keyed", proposal);
        var second = await ProposeAsync("version-keyed", proposal);

        Assert.Equal(WireClaimStatus.Accepted, first.Status);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Head, second.Head);

        var head = await GetAsync("/v1/versions/version-keyed/head", WireJson.Default.WireVersionHead);

        Assert.Equal(1, head.Head);
    }

    [Fact]
    public async Task ADifferentKeyIsAnotherCommand()
    {
        var first = await ProposeAsync(
            "version-two-keys",
            Proposal("claim-first-key", LedgerIds.New()));

        var second = await ProposeAsync(
            "version-two-keys",
            Proposal("claim-second-key", LedgerIds.New()));

        Assert.Equal(WireClaimStatus.Accepted, first.Status);
        Assert.Equal(WireClaimStatus.Accepted, second.Status);

        var head = await GetAsync("/v1/versions/version-two-keys/head", WireJson.Default.WireVersionHead);

        Assert.Equal(2, head.Head);
    }

    /// <summary>A key that is not a ULID is the caller's fault, and it is refused before anything is written.</summary>
    [Fact]
    public async Task AKeyThatIsNotAUlidIsRefusedBeforeAnythingIsWritten()
    {
        using var response = await _client.PostAsJsonAsync(
            "/v1/versions/version-bad-key/claims",
            Proposal("claim-bad-key", "not-a-key"),
            WireJson.Default.WireClaimProposal);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.InvalidRequestProblem, problem.Type);
        Assert.Contains("idempotency_key", problem.Detail, StringComparison.Ordinal);

        var head = await GetAsync("/v1/versions/version-bad-key/head", WireJson.Default.WireVersionHead);

        Assert.Equal(0, head.Head);
    }

    private static WireClaimProposal Proposal(string claimId, string key) =>
        new(
            claimId,
            WireClaimTypes.Fact,
            VendorShape,
            Vendor(claimId),
            "the supplier is north",
            "tester",
            key);

    /// <summary>
    /// A key belongs to the command rather than to one family of commands: every command that records something is keyed
    /// the same way, so a retry is answered rather than done again. This one reports a different total, which is what a
    /// lost answer looks like from the caller's side, and it must be told what landed rather than have a second
    /// measurement recorded under its key.
    /// </summary>
    [Fact]
    public async Task ARetriedCounterUnderOneKeyIsAnsweredWithTheTotalThatLanded()
    {
        string key = LedgerIds.New();

        using var first = await _client.PostAsJsonAsync(
            "/v1/versions/version-counter-keyed/counters",
            new WireCounterRecording("the bell", 4, 6, key),
            WireJson.Default.WireCounterRecording);

        var recorded = (await first.Content.ReadFromJsonAsync(WireJson.Default.WireCounter))!;

        Assert.Equal(4, recorded.Total);

        using var retried = await _client.PostAsJsonAsync(
            "/v1/versions/version-counter-keyed/counters",
            new WireCounterRecording("the bell", 9, 6, key),
            WireJson.Default.WireCounterRecording);

        var answered = (await retried.Content.ReadFromJsonAsync(WireJson.Default.WireCounter))!;

        Assert.Equal(recorded, answered);

        var counters = await GetAsync("/v1/versions/version-counter-keyed/counters", WireJson.Default.WireCounterList);

        Assert.Equal(4, Assert.Single(counters.Counters).Total);
    }

    /// <summary>A retry that carries a different value is a lost answer, not a second lock.</summary>
    [Fact]
    public async Task ARetriedLockUnderOneKeyIsAnsweredWithTheValueThatWasLocked()
    {
        string key = LedgerIds.New();

        using var locked = await _client.PostAsJsonAsync(
            "/v1/versions/version-anchor-keyed/anchors",
            new WireAnchorLock("service", "api_version", "v2", string.Empty, string.Empty, key),
            WireJson.Default.WireAnchorLock);

        var anchor = (await locked.Content.ReadFromJsonAsync(WireJson.Default.WireAnchor))!;

        Assert.Equal("v2", anchor.LockedValue);

        using var retried = await _client.PostAsJsonAsync(
            "/v1/versions/version-anchor-keyed/anchors",
            new WireAnchorLock("service", "api_version", "v3", string.Empty, string.Empty, key),
            WireJson.Default.WireAnchorLock);

        Assert.Equal("v2", (await retried.Content.ReadFromJsonAsync(WireJson.Default.WireAnchor))!.LockedValue);

        var anchors = await GetAsync("/v1/versions/version-anchor-keyed/anchors", WireJson.Default.WireAnchorList);

        Assert.Equal("v2", Assert.Single(anchors.Anchors).LockedValue);
    }

    /// <summary>
    /// A promise key identifies the obligation, so a retry under one key opens one promise rather than a second one - and
    /// is answered rather than refused as a key that is already open.
    /// </summary>
    [Fact]
    public async Task ARetriedPromiseUnderOneKeyOpensOnePromise()
    {
        string key = LedgerIds.New();

        WirePromiseRegistration registration =
            new("audit-report", "deliverable", "an audit report", "release", "compliance", key);

        using var opened = await _client.PostAsJsonAsync(
            "/v1/versions/version-promise-keyed/promises", registration, WireJson.Default.WirePromiseRegistration);

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        var promise = (await opened.Content.ReadFromJsonAsync(WireJson.Default.WirePromise))!;

        using var retried = await _client.PostAsJsonAsync(
            "/v1/versions/version-promise-keyed/promises", registration, WireJson.Default.WirePromiseRegistration);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);

        var answered = (await retried.Content.ReadFromJsonAsync(WireJson.Default.WirePromise))!;

        Assert.Equal(promise.PromiseId, answered.PromiseId);

        var open = await GetAsync("/v1/versions/version-promise-keyed/promises", WireJson.Default.WirePromiseList);

        Assert.Single(open.Promises);
    }

    /// <summary>
    /// Nothing is locked at the second attempt, so an unkeyed release would answer "nothing was released" - a different
    /// answer to the same command. The retry is owed the answer of the attempt that removed the lock.
    /// </summary>
    [Fact]
    public async Task ARetriedReleaseUnderOneKeyIsNotAnsweredAsNothingReleased()
    {
        string locked = LedgerIds.New();
        string released = LedgerIds.New();
        string path = "/v1/versions/version-release-keyed/anchors/service.api_version/release";

        await _client.PostAsJsonAsync(
            "/v1/versions/version-release-keyed/anchors",
            new WireAnchorLock("service", "api_version", "v2", string.Empty, string.Empty, locked),
            WireJson.Default.WireAnchorLock);

        using var first = await _client.PostAsync($"{path}?idempotency_key={released}", content: null);

        Assert.True((await first.Content.ReadFromJsonAsync(WireJson.Default.WireAnchorRelease))!.Released);

        using var retried = await _client.PostAsync($"{path}?idempotency_key={released}", content: null);

        Assert.True((await retried.Content.ReadFromJsonAsync(WireJson.Default.WireAnchorRelease))!.Released);
    }

    /// <summary>
    /// The promise is settled at the second attempt, so an unkeyed fulfilment would answer "not fulfilled": the retry has
    /// to be told that the fulfilment it is retrying is the one that settled the obligation.
    /// </summary>
    [Fact]
    public async Task ARetriedFulfilmentUnderOneKeyIsNotSettledTwice()
    {
        string key = LedgerIds.New();
        string path = "/v1/versions/version-fulfil-keyed/promises/audit-report/fulfill";

        await _client.PostAsJsonAsync(
            "/v1/versions/version-fulfil-keyed/promises",
            new WirePromiseRegistration("audit-report", "deliverable", "an audit report", "release", "compliance"),
            WireJson.Default.WirePromiseRegistration);

        using var first = await _client.PostAsync($"{path}?idempotency_key={key}", content: null);

        Assert.True((await first.Content.ReadFromJsonAsync(WireJson.Default.WirePromiseFulfilment))!.Fulfilled);

        using var retried = await _client.PostAsync($"{path}?idempotency_key={key}", content: null);

        Assert.True((await retried.Content.ReadFromJsonAsync(WireJson.Default.WirePromiseFulfilment))!.Fulfilled);
    }

    /// <summary>
    /// A version is a claim, so creating one twice is refused because the identity is taken - unless it is the same
    /// command retried, which the key says it is.
    /// </summary>
    [Fact]
    public async Task ARetriedVersionCreationIsAnsweredWithTheVersionItCreated()
    {
        string key = LedgerIds.New();

        using var first = await PostVersionAsync(
            new WireVersionRequest("version-created-keyed", string.Empty, string.Empty, "keyed", "tester", key));

        var created = (await first.Content.ReadFromJsonAsync(WireJson.Default.WireVersion))!;

        using var retried = await PostVersionAsync(
            new WireVersionRequest("version-created-keyed", string.Empty, string.Empty, "keyed", "tester", key));

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(created, (await retried.Content.ReadFromJsonAsync(WireJson.Default.WireVersion))!);
    }

    /// <summary>
    /// With no id given, the version is minted during the command and the caller cannot name it on a retry: the key is the
    /// only thing that can make the retry land on the version that was created rather than on a second one.
    /// </summary>
    [Fact]
    public async Task AVersionWithAGeneratedIdCanBeRetriedUnderOneKey()
    {
        string key = LedgerIds.New();

        using var first = await PostVersionAsync(
            new WireVersionRequest(string.Empty, string.Empty, string.Empty, "generated", "tester", key));

        var created = (await first.Content.ReadFromJsonAsync(WireJson.Default.WireVersion))!;

        using var retried = await PostVersionAsync(
            new WireVersionRequest(string.Empty, string.Empty, string.Empty, "generated", "tester", key));

        var answered = (await retried.Content.ReadFromJsonAsync(WireJson.Default.WireVersion))!;

        Assert.Equal(created.VersionId, answered.VersionId);

        var head = await GetAsync($"/v1/versions/{created.VersionId}/head", WireJson.Default.WireVersionHead);

        // One claim, which is the version's own: the retry added nothing to the ledger.
        Assert.Equal(1, head.Head);
    }

    [Fact]
    public async Task AnIndexVersionThatIsNotRecordedIsNotFound()
    {
        using var response = await _client.GetAsync(new Uri("/v1/indexes/idx-0000000000000000", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.UnknownIndexVersionProblem, problem.Type);
    }

    [Fact]
    public async Task ACollectionWithNoLiveVersionHasNone()
    {
        using var response = await _client.GetAsync(
            new Uri("/v1/indexes/active?collection_id=col-nothing", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The media type a DOCX declares.</summary>
    private const string DocxMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Offers a document as bytes, which is one of the two ways a document travels.</summary>
    private static WireSourceIngest AsBytes(string path, string mediaType, byte[] bytes) =>
        new(path, mediaType, null, string.Empty, Convert.ToBase64String(bytes));

    /// <summary>Builds a DOCX around its paragraphs, which is all the extractor reads.</summary>
    private static byte[] Docx(params string[] paragraphs)
    {
        var xml = new StringBuilder("""<w:document xmlns:w="x"><w:body>""");

        foreach (var paragraph in paragraphs)
        {
            xml.Append($"<w:p><w:r><w:t>{paragraph}</w:t></w:r></w:p>");
        }

        xml.Append("</w:body></w:document>");

        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("word/document.xml").Open();

            entry.Write(Encoding.UTF8.GetBytes(xml.ToString()));
        }

        return buffer.ToArray();
    }

    /// <summary>Builds a one-page PDF with a text layer - or without one when the text is empty.</summary>
    private static byte[] Pdf(string text)
    {
        var content = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> "
                + "/Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();

        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xrefAt = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:0000000000} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefAt}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }}
