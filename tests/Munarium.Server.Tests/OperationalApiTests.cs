namespace Munarium.Server.Tests;

using Munarium.Wire;

/// <summary>Tests for the operational surface: what a deployment says about itself.</summary>
public class OperationalApiTests
{
    /// <summary>A deployment answers who it is, with the contract and the version both.</summary>
    [Fact]
    public void AVDeploymentSaysWhatItIs()
    {
        var version = MunariumOperations.Version();

        Assert.Equal(MunariumOperations.Contract, version.Contract);
        Assert.Equal(MunariumOperations.Health().Contract, version.Contract);
        Assert.False(string.IsNullOrWhiteSpace(version.Version));
    }

    /// <summary>
    /// What a deployment serves as its contract is the specification this build was generated from, converted
    /// rather than restated - so a caller that generates a client from a running instance reads the document
    /// this repository is held to.
    /// </summary>
    [Fact]
    public void TheServedContractIsTheDocumentThePortIsBuiltFrom()
    {
        using var document = JsonDocument.Parse(MunariumOperations.OpenApi());

        var root = document.RootElement;

        // What a generator starts with: which OpenAPI dialect this is, and which document it is.
        Assert.Equal("3.0.3", root.GetProperty("openapi").GetString());
        Assert.Equal("Munarium", root.GetProperty("info").GetProperty("title").GetString());

        // The windows a deployment is probed through, and the ledger's point read, are declared paths - a route
        // added to the application without the document is a route nobody can generate a client for.
        var paths = root.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/healthz", out _));
        Assert.True(paths.TryGetProperty("/readyz", out _));
        Assert.True(paths.TryGetProperty("/openapi.json", out _));
        Assert.True(paths.TryGetProperty("/v1/claims/{claim_id}", out _));

        // Converted from the YAML the contract is written as, not served as YAML and not flattened into text:
        // the operation a caller reads here is the operation the document names.
        Assert.Equal(
            "GetClaim",
            paths.GetProperty("/v1/claims/{claim_id}").GetProperty("get").GetProperty("operationId").GetString());
    }
}