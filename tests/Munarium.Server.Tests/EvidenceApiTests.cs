namespace Munarium.Server.Tests;

using System.Net;
using Munarium.Wire;

/// <summary>
/// The sealed evidence plane over HTTP: the artifact path as a caller sees it, with the statuses and the JSON the
/// contract declares.
/// </summary>
/// <remarks>
/// The behaviour itself is tested over the operation surface, where both transports meet; what is tested here is that
/// the routes, the bindings and the encodings are what the contract says they are.
/// </remarks>
public class EvidenceApiTests(MunariumApiFactory factory) : IClassFixture<MunariumApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task ASealedArtifactResolvesToItsManifestAndRowsOverHttp()
    {
        var (manifest, bytes) = EvidenceFixture.Artifact("http-approved");

        using var sealResponse = await _client.PostAsJsonAsync(
            "/v1/evidence",
            new WireSealEvidenceRequest(manifest, Convert.ToBase64String(bytes)),
            WireJson.Default.WireSealEvidenceRequest);

        Assert.Equal(HttpStatusCode.OK, sealResponse.StatusCode);

        var seal = (await sealResponse.Content.ReadFromJsonAsync(WireJson.Default.WireSealResponse))!;

        Assert.True(seal.Created);
        Assert.Equal("committed", seal.State);
        Assert.StartsWith("ev-", seal.EvidenceId, StringComparison.Ordinal);

        using var manifestResponse = await _client.GetAsync(
            new Uri($"/v1/evidence/{seal.EvidenceId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);

        var json = await manifestResponse.Content.ReadAsStringAsync();

        // The names are the contract's, and `bytes_len` is the one this port had wrong until the contract was read.
        Assert.Contains("\"bytes_len\":", json, StringComparison.Ordinal);
        Assert.Contains($"\"evidence_id\":\"{seal.EvidenceId}\"", json, StringComparison.Ordinal);

        var read = (await manifestResponse.Content.ReadFromJsonAsync(WireJson.Default.EvidenceManifest))!;

        Assert.Equal(manifest.ComputeDomainKey(), read.ComputeDomainKey());

        using var rowsResponse = await _client.GetAsync(
            new Uri($"/v1/evidence/{seal.EvidenceId}/rows?from=0&limit=10", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, rowsResponse.StatusCode);

        var rows = (await rowsResponse.Content.ReadFromJsonAsync(WireJson.Default.WireEvidenceRows))!;

        Assert.Equal(1, rows.Total);
        Assert.False(rows.HasMore);

        // A row is keyed by the names the manifest declares, not by the header the bytes happened to carry.
        var row = Assert.Single(rows.Rows);

        Assert.Equal("v-1", row["vendor_id"]);
        Assert.Equal("http-approved", row["status"]);

        var accesses = (await _client.GetFromJsonAsync(
            new Uri($"/v1/evidence/{seal.EvidenceId}/accesses", UriKind.Relative),
            WireJson.Default.WireEvidenceAccessList))!;

        Assert.Contains(accesses.Accesses, access => access.Kind == "manifest" && access.Outcome == "ok");
        Assert.Contains(accesses.Accesses, access => access.Kind == "rows" && access.Outcome == "ok");
    }

    [Fact]
    public async Task APurgeAnswersExpiredRatherThanMissingOverHttp()
    {
        var (manifest, bytes) = EvidenceFixture.Artifact("http-purged");

        using var sealResponse = await _client.PostAsJsonAsync(
            "/v1/evidence",
            new WireSealEvidenceRequest(manifest, Convert.ToBase64String(bytes)),
            WireJson.Default.WireSealEvidenceRequest);

        var seal = (await sealResponse.Content.ReadFromJsonAsync(WireJson.Default.WireSealResponse))!;

        using var purge = await _client.DeleteAsync(new Uri($"/v1/evidence/{seal.EvidenceId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);

        var purged = (await purge.Content.ReadFromJsonAsync(WireJson.Default.WireEvidencePurge))!;

        Assert.True(purged.Purged);
        Assert.Equal("purged", purged.State);

        // A purged artifact keeps its row, so the citation resolves as expired - an honest statement about retention -
        // rather than as not found, which would read as though the citation had been fabricated.
        using var after = await _client.GetAsync(new Uri($"/v1/evidence/{seal.EvidenceId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Gone, after.StatusCode);

        var problem = (await after.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.EvidenceExpiredProblem, problem.Type);
    }

    [Fact]
    public async Task TheGrantFlowCommitsOverHttp()
    {
        var (manifest, bytes) = EvidenceFixture.Artifact("http-granted");

        using var sealResponse = await _client.PostAsJsonAsync(
            "/v1/evidence",
            new WireSealEvidenceRequest(manifest),
            WireJson.Default.WireSealEvidenceRequest);

        var seal = (await sealResponse.Content.ReadFromJsonAsync(WireJson.Default.WireSealResponse))!;
        var grant = seal.Grant ?? throw new InvalidOperationException("a seal with no bytes has to issue a grant.");

        Assert.Equal("pending", seal.State);

        // A pending artifact is not evidence yet, and the contract gives that its own answer rather than a 404.
        using var pending = await _client.GetAsync(new Uri($"/v1/evidence/{seal.EvidenceId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.Equal(
            MunariumOperations.EvidencePendingProblem,
            (await pending.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!.Type);

        using var upload = await _client.PutAsJsonAsync(
            $"/v1/evidence/{seal.EvidenceId}/bytes?grant={grant.GrantId}",
            new WireEvidenceBytesUpload(Convert.ToBase64String(bytes)),
            WireJson.Default.WireEvidenceBytesUpload);

        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);

        using var commit = await _client.PostAsync($"/v1/evidence/{seal.EvidenceId}/commit", content: null);

        var committed = (await commit.Content.ReadFromJsonAsync(WireJson.Default.WireEvidenceCommit))!;

        Assert.True(committed.Committed);
        Assert.Equal("committed", committed.State);

        using var manifestResponse = await _client.GetAsync(
            new Uri($"/v1/evidence/{seal.EvidenceId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
    }

    [Fact]
    public async Task BytesThatAreNotTheManifestAreRefusedOverHttp()
    {
        var (manifest, _) = EvidenceFixture.Artifact("http-mismatched");

        using var response = await _client.PostAsJsonAsync(
            "/v1/evidence",
            new WireSealEvidenceRequest(manifest, Convert.ToBase64String("v-9,denied\n"u8.ToArray())),
            WireJson.Default.WireSealEvidenceRequest);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem))!;

        Assert.Equal(MunariumOperations.EvidenceHashMismatchProblem, problem.Type);
        Assert.Contains(manifest.ArtifactHash, problem.Detail, StringComparison.Ordinal);
    }
}
