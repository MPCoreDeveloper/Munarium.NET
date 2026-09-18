namespace Munarium.Server.Tests;

using Munarium.Context;
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
        Assert.Equal("munarium@1", result.Envelope.IndexVersion);
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
    [Fact]
    public async Task ADocumentWithNoExtractorIsRefusedAndNothingIsStored()
    {
        using var put = await _client.PutAsJsonAsync(
            "/v1/sources",
            new WireSourceIngest("docs/scan.pdf", "application/pdf", "%PDF-1.7", string.Empty),
            WireJson.Default.WireSourceIngest);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, put.StatusCode);

        var problem = (await put.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.UnsupportedMediaTypeProblem, problem.Type);
        Assert.Contains("application/pdf", problem.Detail, StringComparison.Ordinal);

        using var missing = await _client.GetAsync(
            new Uri($"/v1/sources/{SourceKey.Id(MunariumKernel.Tenant, "docs/scan.pdf")}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
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

    [Fact]
    public async Task ASourceThatWasNeverIngestedHasNoRow()
    {
        using var response = await _client.GetAsync(new Uri("/v1/sources/src-0000000000000000", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.UnknownSourceProblem, problem.Type);
    }
}
