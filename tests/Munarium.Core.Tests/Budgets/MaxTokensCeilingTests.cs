namespace Munarium.Core.Tests.Budgets;

using Munarium.Budgets;
using Munarium.Core.Tests.Support;

/// <summary>
/// The ceilings a deployment's paid calls are held to: the built-ins, the process's own variables, and a tenant's
/// replacement in front of both.
/// </summary>
public class MaxTokensCeilingTests
{
    private const string Tenant = "acme";

    [Fact]
    public void TheBuiltInsAreTheOriginals()
    {
        var builtin = MaxTokensBudget.Builtin;

        Assert.Equal(2048, builtin.TurnCompletion);
        Assert.Equal(256, builtin.QueryExpansion);
        Assert.Equal(1024, builtin.CompleteDefault);
        Assert.Equal(512, builtin.HealthAiProbe);
        Assert.Equal(32, builtin.HierarchyClassifier);
        Assert.Equal(480, builtin.HierarchyIntent);
        Assert.Equal(2048, builtin.RunbookAdvisory);
        Assert.Equal(8192, builtin.AuthoringAssist);
        Assert.Null(MaxTokensBudget.Refusal(builtin));
    }

    [Fact]
    public void AProcessVariableOverridesOneCeilingAndLeavesTheRest()
    {
        var budgets = MaxTokensBudget.Between(
            MaxTokensBudget.Builtin,
            name => name == "MUNARIUM_MAX_TOKENS_TURN_COMPLETION" ? " 4096 " : null);

        Assert.Equal(4096, budgets.TurnCompletion);
        Assert.Equal(MaxTokensBudget.Builtin.QueryExpansion, budgets.QueryExpansion);
        Assert.Equal(MaxTokensBudget.Builtin.AuthoringAssist, budgets.AuthoringAssist);
    }

    [Fact]
    public void AProcessVariableThatIsNotACeilingIsRefusedByName()
    {
        var malformed = Assert.Throws<InvalidOperationException>(() => MaxTokensBudget.Between(
            MaxTokensBudget.Builtin,
            name => name == "MUNARIUM_MAX_TOKENS_QUERY_EXPANSION" ? "lots" : null));

        Assert.Equal("MUNARIUM_MAX_TOKENS_QUERY_EXPANSION must be an integer, and it is set to 'lots'", malformed.Message);

        var outOfRange = Assert.Throws<InvalidOperationException>(() => MaxTokensBudget.Between(
            MaxTokensBudget.Builtin,
            name => name == "MUNARIUM_MAX_TOKENS_QUERY_EXPANSION" ? "4096" : null));

        Assert.Contains(
            "query_expansion must be between 32 and 512",
            outOfRange.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCeilingIsBoundedTheWayTheOriginalBoundsIt()
    {
        Assert.Equal(
            "turn_completion must be between 256 and 16384, and it is 255",
            MaxTokensBudget.Refusal(MaxTokensBudget.Builtin with { TurnCompletion = 255 }));

        Assert.Equal(
            "query_expansion must be between 32 and 512, and it is 513",
            MaxTokensBudget.Refusal(MaxTokensBudget.Builtin with { QueryExpansion = 513 }));

        Assert.Equal(
            "complete_default must be between 1 and 65536, and it is 0",
            MaxTokensBudget.Refusal(MaxTokensBudget.Builtin with { CompleteDefault = 0 }));

        Assert.Equal(
            "authoring_assist must be between 1 and 65536, and it is 65537",
            MaxTokensBudget.Refusal(MaxTokensBudget.Builtin with { AuthoringAssist = 65_537 }));
    }

    [Fact]
    public async Task TheProcessDefaultsApplyUntilATenantReplacesThem()
    {
        var ceiling = new MaxTokensCeiling(
            new InMemoryMaxTokensStore(),
            MaxTokensBudget.Builtin with { TurnCompletion = 4096 });

        var effective = await ceiling.EffectiveAsync(Tenant);

        Assert.Equal(4096, effective.Budgets.TurnCompletion);
        Assert.Equal(MaxTokensCeiling.EnvironmentSource, effective.Source);
        Assert.Null(effective.UpdatedAt);
    }

    [Fact]
    public async Task AReplacementWinsOverTheProcessDefaultsAndCarriesTheInstantItWasMade()
    {
        var ceiling = new MaxTokensCeiling(new InMemoryMaxTokensStore(), MaxTokensBudget.Builtin);
        var asked = MaxTokensBudget.Builtin with { TurnCompletion = 8192, HealthAiProbe = 1024 };

        var replaced = await ceiling.ReplaceAsync(Tenant, asked, new DateTimeOffset(2026, 9, 20, 9, 30, 0, TimeSpan.Zero));

        Assert.Equal(MaxTokensCeiling.TenantSource, replaced.Source);
        Assert.Equal("2026-09-20T09:30:00Z", replaced.UpdatedAt);
        Assert.Equal(8192, replaced.Budgets.TurnCompletion);

        var effective = await ceiling.EffectiveAsync(Tenant);

        Assert.Equal(8192, effective.Budgets.TurnCompletion);
        Assert.Equal(1024, effective.Budgets.HealthAiProbe);
        Assert.Equal(MaxTokensCeiling.TenantSource, effective.Source);

        // Another tenant is untouched: a replacement belongs to the tenant that made it.
        Assert.Equal(MaxTokensCeiling.EnvironmentSource, (await ceiling.EffectiveAsync("other")).Source);
    }

    [Fact]
    public void AnInstantIsCarriedTheWayTheContractSpellsIt() =>
        Assert.Equal(
            "2026-09-20T09:30:00Z",
            MaxTokensCeiling.Timestamp(new DateTimeOffset(2026, 9, 20, 11, 30, 0, TimeSpan.FromHours(2))));
}
