namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Providers;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbProviderDeclarations"/>: the declarations a deployment applied, kept where a restart
/// still finds them.
/// </summary>
/// <remarks>
/// What these have to get right is the round trip of a document whose fields are optional: a tier override that is
/// absent, a credential that lives in a file rather than a variable, and a model list with more than one entry.
/// </remarks>
public class SharpCoreDbProviderDeclarationsTests
{
    private const string Tenant = "acme";

    [Fact]
    public async Task ADeclarationIsReadBackInFull()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.ProviderDeclarations.SaveAsync(Tenant, Declaration());

        var read = await fixture.ProviderDeclarations.FindAsync(Tenant, "halvard-anthropic");

        Assert.NotNull(read);
        Assert.Equal(ProviderFamilies.Anthropic, read.Family);
        Assert.Equal("https://anthropic.example/v1", read.Endpoint);
        Assert.Equal(["claude-sonnet-5", "claude-haiku-4-5"], read.Models.Complete);
        Assert.Equal(["voyage-3"], read.Models.Embed);
        Assert.Equal("claude-haiku-local", read.Models.Fast);
        Assert.Null(read.Models.Frontier);
        Assert.Equal("MUNARIUM_SECRET_ANTHROPIC", read.Credential?.EnvironmentVariable);
        Assert.Null(read.Credential?.FilePath);
    }

    [Fact]
    public async Task ApplyingTheSameNameAgainReplacesIt()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.ProviderDeclarations.SaveAsync(Tenant, Declaration());
        await fixture.ProviderDeclarations.SaveAsync(
            Tenant,
            Declaration() with { Endpoint = "https://elsewhere.example/v1" });

        var read = await fixture.ProviderDeclarations.FindAsync(Tenant, "halvard-anthropic");

        Assert.Equal("https://elsewhere.example/v1", read?.Endpoint);
        Assert.Single(await fixture.ProviderDeclarations.ListAsync(Tenant));
    }

    [Fact]
    public async Task AKeyInAFileAndAMissingCredentialBothSurviveTheRow()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.ProviderDeclarations.SaveAsync(
            Tenant,
            Declaration() with { Credential = CredentialReference.ForFile("/run/secrets/anthropic") });
        await fixture.ProviderDeclarations.SaveAsync(
            Tenant,
            Declaration() with { Name = "office-ollama", Credential = null });

        var fromFile = await fixture.ProviderDeclarations.FindAsync(Tenant, "halvard-anthropic");
        var local = await fixture.ProviderDeclarations.FindAsync(Tenant, "office-ollama");

        Assert.Equal("/run/secrets/anthropic", fromFile?.Credential?.FilePath);
        Assert.Null(fromFile?.Credential?.EnvironmentVariable);
        Assert.Null(local?.Credential);
    }

    [Fact]
    public async Task OneTenantsDeclarationIsNotAnothers()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.ProviderDeclarations.SaveAsync(Tenant, Declaration());

        Assert.Null(await fixture.ProviderDeclarations.FindAsync("other", "halvard-anthropic"));
        Assert.Empty(await fixture.ProviderDeclarations.ListAsync("other"));
    }

    [Fact]
    public async Task TheDeclarationsOutliveTheConnectionThatWroteThem()
    {
        var fixture = SourceStoreFixture.Create();
        var path = fixture.DatabasePath;

        await fixture.ProviderDeclarations.SaveAsync(Tenant, Declaration());
        await fixture.DisposeAsync();

        await using var reopened = SourceStoreFixture.Create(path);
        var read = await reopened.ProviderDeclarations.FindAsync(Tenant, "halvard-anthropic");

        Assert.Equal("halvard-anthropic", read?.Name);
        Assert.Equal("MUNARIUM_SECRET_ANTHROPIC", read?.Credential?.EnvironmentVariable);
        Assert.Equal(["claude-sonnet-5", "claude-haiku-4-5"], read?.Models.Complete);
    }

    [Fact]
    public async Task AAConfigurationsDeclaredBudgetsSurviveTheRow()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.ProviderDeclarations.SaveAsync(
            Tenant,
            Declaration() with
            {
                Budgets = new ProviderBudgets
                {
                    RequestsPerMinute = 60,
                    TokensPerMinute = 120_000,
                    Capable = 5_000_000,
                },
            });

        var read = await fixture.ProviderDeclarations.FindAsync(Tenant, "halvard-anthropic");

        Assert.Equal(60, read?.Budgets.RequestsPerMinute);
        Assert.Equal(120_000, read?.Budgets.TokensPerMinute);
        Assert.Equal(5_000_000, read?.Budgets.Capable);
        Assert.Null(read?.Budgets.Fast);
        Assert.Null(read?.Budgets.Frontier);

        // A configuration that declares none reads back as unbudgeted rather than as a ceiling of zero.
        await fixture.ProviderDeclarations.SaveAsync(Tenant, Declaration() with { Name = "office-unbudgeted" });

        var unbudgeted = await fixture.ProviderDeclarations.FindAsync(Tenant, "office-unbudgeted");

        Assert.True(unbudgeted?.Budgets.IsEmpty);
    }

    /// <summary>The document a deployment applied, as one value.</summary>
    /// <returns>The declaration.</returns>
    private static ProviderDeclaration Declaration() => new()
    {
        Name = "halvard-anthropic",
        Provider = ProviderId.Anthropic,
        Endpoint = "https://anthropic.example/v1",
        Models = new ProviderModels
        {
            Complete = ["claude-sonnet-5", "claude-haiku-4-5"],
            Embed = ["voyage-3"],
            Fast = "claude-haiku-local",
        },
        Credential = CredentialReference.ForEnvironment("MUNARIUM_SECRET_ANTHROPIC"),
    };
}
