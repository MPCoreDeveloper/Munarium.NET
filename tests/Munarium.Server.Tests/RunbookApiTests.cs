namespace Munarium.Server.Tests;

using System.Net;
using System.Net.Http.Json;
using Munarium.Wire;

/// <summary>
/// The runbook operations over the JSON surface: applying what an operator wrote, and listing what the deployment holds.
/// </summary>
public class RunbookApiTests : IClassFixture<MunariumApiFactory>
{
    private readonly HttpClient _client;

    /// <summary>Creates the test over the real application.</summary>
    /// <param name="factory">The application under test.</param>
    public RunbookApiTests(MunariumApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _client = factory.CreateClient();
    }

    /// <summary>
    /// The worked example applies, its reference is the pin a session will name, and the version is listed afterwards -
    /// which is what makes it reachable by name.
    /// </summary>
    [Fact]
    public async Task AWorkedExampleIsAppliedAndListed()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/runbooks",
            new WireRunbookApply(Example()),
            WireJson.Default.WireRunbookApply);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var applied = await response.Content.ReadFromJsonAsync(WireJson.Default.WireAppliedRunbook);

        Assert.NotNull(applied);
        Assert.Contains("@", applied.RunbookRef, StringComparison.Ordinal);
        Assert.Equal("active", applied.Status);
        Assert.True(applied.Version > 0);
        Assert.Equal(applied.RunbookRef, $"{applied.Name}@{applied.Version}");
        Assert.False(string.IsNullOrWhiteSpace(applied.CreatedAt));

        var listed = await GetAsync("/v1/runbooks");

        Assert.Contains(listed.Runbooks, runbook => runbook.RunbookRef == applied.RunbookRef);

        // A removed version is hidden unless the caller asks for it, and nothing here has been removed - so the two
        // reads agree, which is the least a list can be asked for.
        var withRemoved = await GetAsync("/v1/runbooks?include_removed=true");

        Assert.Equal(listed.Runbooks.Count, withRemoved.Runbooks.Count);
    }

    /// <summary>
    /// A document that is not a runbook is refused with the problem its code names, and nothing is stored: the refusal
    /// carries the reader's own complaint, because the operator editing YAML wants the line rather than a summary.
    /// </summary>
    [Fact]
    public async Task ADocumentThatIsNotARunbookIsRefused()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/runbooks",
            new WireRunbookApply("kind: Something\napiVersion: v1\nmetadata:\n  name: nope\n  version: 1\n"),
            WireJson.Default.WireRunbookApply);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.NotNull(problem);
        Assert.Equal(MunariumOperations.RunbookInvalidProblem, problem.Type);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
    }

    private async Task<WireRunbookList> GetAsync(string path)
    {
        var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var listed = await response.Content.ReadFromJsonAsync(WireJson.Default.WireRunbookList);

        return Assert.IsType<WireRunbookList>(listed);
    }

    /// <summary>Reads the worked example the original ships, which is the conformance fixture.</summary>
    /// <returns>The YAML, as written.</returns>
    private static string Example()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "contract", "lab", "example", "runbook.yaml");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("contract/lab/example/runbook.yaml was not found above the test binary.");
    }
}
