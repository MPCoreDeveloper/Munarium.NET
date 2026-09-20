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
}