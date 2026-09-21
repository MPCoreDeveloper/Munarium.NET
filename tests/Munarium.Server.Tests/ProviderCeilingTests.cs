namespace Munarium.Server.Tests;

using Munarium.Budgets;
using Munarium.Wire;

/// <summary>
/// What a deployment that has replaced nothing reports: the ceilings of the process it was composed in.
/// </summary>
/// <remarks>
/// A class of its own, over a database of its own, because "nothing has been replaced yet" is a claim about the
/// deployment rather than about this test - and in the class that also replaces the ceilings it is a claim about the
/// order the tests happen to run in. Measured, after the runner disagreed: the claim read "environment" on one machine
/// and "tenant" on another, in the same commit, because the replacement ran first there.
/// </remarks>
public class ProviderCeilingTests(MunariumApiFactory factory) : IClassFixture<MunariumApiFactory>
{
    private readonly MunariumApiFactory _factory = factory;

    /// <summary>A composed deployment reports the process ceilings, with no instant to name.</summary>
    [Fact]
    public async Task ADeploymentThatHasReplacedNothingReportsTheCeilingsOfItsProcess()
    {
        using var client = _factory.CreateClient();

        var read = await client.GetFromJsonAsync(
            new Uri("/v1/max-tokens", UriKind.Relative),
            WireJson.Default.WireMaxTokens);

        Assert.NotNull(read);

        // No replacement has been stored, which is what the source says and why there is no instant: nothing happened
        // that a time could be attached to.
        Assert.Equal(MaxTokensCeiling.EnvironmentSource, read.Source);
        Assert.Null(read.UpdatedAt);

        // The values are this process's composition - the built-ins with its own MUNARIUM_MAX_TOKENS_* variables laid
        // over them - computed here rather than asserted against the built-ins, so the test holds the endpoint to what
        // it promised instead of to what this machine happens to set.
        var composed = MaxTokensBudget.Between(MaxTokensBudget.Builtin, Environment.GetEnvironmentVariable);

        Assert.Equal(composed.TurnCompletion, read.Budgets.TurnCompletion);
        Assert.Equal(composed.QueryExpansion, read.Budgets.QueryExpansion);
        Assert.Equal(composed.CompleteDefault, read.Budgets.CompleteDefault);
        Assert.Equal(composed.HealthAiProbe, read.Budgets.HealthAiProbe);
        Assert.Equal(composed.HierarchyClassifier, read.Budgets.HierarchyClassifier);
        Assert.Equal(composed.HierarchyIntent, read.Budgets.HierarchyIntent);
        Assert.Equal(composed.RunbookAdvisory, read.Budgets.RunbookAdvisory);
        Assert.Equal(composed.AuthoringAssist, read.Budgets.AuthoringAssist);
    }
}
