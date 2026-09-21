namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Budgets;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbMaxTokens"/>: a tenant's replacement of the ceilings its paid calls are held to.
/// </summary>
/// <remarks>
/// What these have to get right is that the set is replaced whole and that the instant travels with it, because an
/// operator reading the ceilings back is asking two questions at once - what applies, and since when.
/// </remarks>
public class SharpCoreDbMaxTokensTests
{
    private const string Tenant = "acme";

    [Fact]
    public async Task ACeilingSetIsReadBackAsItWasWritten()
    {
        await using var fixture = SourceStoreFixture.Create();
        var asked = MaxTokensBudget.Builtin with { TurnCompletion = 4096, AuthoringAssist = 16_384 };

        await fixture.MaxTokens.ReplaceAsync(Tenant, asked, new DateTimeOffset(2026, 9, 20, 9, 30, 0, TimeSpan.Zero));

        var read = await fixture.MaxTokens.FindAsync(Tenant);

        Assert.NotNull(read);
        Assert.Equal(4096, read.Budgets.TurnCompletion);
        Assert.Equal(16_384, read.Budgets.AuthoringAssist);
        Assert.Equal(MaxTokensBudget.Builtin.QueryExpansion, read.Budgets.QueryExpansion);
        Assert.Equal("2026-09-20T09:30:00Z", read.UpdatedAt);
    }

    [Fact]
    public async Task AReplacementReplacesTheWholeSet()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.MaxTokens.ReplaceAsync(Tenant, MaxTokensBudget.Builtin, new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero));
        await fixture.MaxTokens.ReplaceAsync(
            Tenant,
            MaxTokensBudget.Builtin with { TurnCompletion = 8192 },
            new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero));

        var read = await fixture.MaxTokens.FindAsync(Tenant);

        Assert.Equal(8192, read?.Budgets.TurnCompletion);
        Assert.Equal("2026-09-20T10:00:00Z", read?.UpdatedAt);
    }

    [Fact]
    public async Task OneTenantsCeilingsAreNotAnothers()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.MaxTokens.ReplaceAsync(Tenant, MaxTokensBudget.Builtin, new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero));

        Assert.Null(await fixture.MaxTokens.FindAsync("other"));
    }

    [Fact]
    public async Task TheCeilingsOutliveTheConnectionThatWroteThem()
    {
        var fixture = SourceStoreFixture.Create();
        var path = fixture.DatabasePath;

        await fixture.MaxTokens.ReplaceAsync(
            Tenant,
            MaxTokensBudget.Builtin with { HealthAiProbe = 1024 },
            new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero));

        await fixture.DisposeAsync();

        await using var reopened = SourceStoreFixture.Create(path);
        var read = await reopened.MaxTokens.FindAsync(Tenant);

        Assert.Equal(1024, read?.Budgets.HealthAiProbe);
    }
}
