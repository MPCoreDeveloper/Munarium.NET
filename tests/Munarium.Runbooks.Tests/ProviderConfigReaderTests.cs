namespace Munarium.Runbooks.Tests;

using Munarium.Providers;

/// <summary>
/// Reading a provider configuration: the declaration an operator applies.
/// </summary>
/// <remarks>
/// The document shape is the original's, field for field, because a configuration written for it has to read here. The
/// two places this port is stricter are asserted as behaviour: a field nobody reads is refused rather than ignored, and
/// a rate budget is refused by name rather than accepted and then not enforced.
/// </remarks>
public class ProviderConfigReaderTests
{
    [Fact]
    public void TheOriginalsDocumentReadsIntoItsDeclaration()
    {
        var declaration = Declaration(
            """
            apiVersion: munarium.ioka.io/v1
            kind: ProviderConfig
            metadata:
              name: halvard-anthropic
            spec:
              provider: anthropic
              endpoint: https://anthropic.example/v1
              models:
                complete: [claude-sonnet-5, claude-haiku-4-5]
                embed: [voyage-3]
                fast: claude-haiku-local
              credentialRef:
                env: MUNARIUM_SECRET_ANTHROPIC
            """);

        Assert.Equal("halvard-anthropic", declaration.Name);
        Assert.Equal(ProviderFamilies.Anthropic, declaration.Family);
        Assert.Equal("https://anthropic.example/v1", declaration.Endpoint);
        Assert.Equal(["claude-sonnet-5", "claude-haiku-4-5"], declaration.Models.Complete);
        Assert.Equal(["voyage-3"], declaration.Models.Embed);
        Assert.Equal("MUNARIUM_SECRET_ANTHROPIC", declaration.Credential?.EnvironmentVariable);

        // The configuration's own tier override wins, and the family's built-in answers where it does not.
        Assert.Equal("claude-haiku-local", declaration.TierModel(ModelTier.Fast));
        Assert.Equal("claude-sonnet-5", declaration.TierModel(ModelTier.Capable));
        Assert.Equal("claude-fable-5-1", declaration.TierModel(ModelTier.Frontier));
    }

    [Fact]
    public void AKeyCanLiveInAFileInsteadOfTheEnvironment()
    {
        var declaration = Declaration(
            """
            apiVersion: munarium.ioka.io/v1
            kind: ProviderConfig
            metadata:
              name: halvard-openai
            spec:
              provider: openai
              models:
                complete: [gpt-5.4]
              credentialRef:
                file: /run/secrets/openai
            """);

        Assert.Null(declaration.Credential?.EnvironmentVariable);
        Assert.Equal("/run/secrets/openai", declaration.Credential?.FilePath);
    }

    [Fact]
    public void ALocalEndpointNeedsNoCredentialAtAll()
    {
        var local = Declaration(
            """
            apiVersion: munarium.ioka.io/v1
            kind: ProviderConfig
            metadata:
              name: office-ollama
            spec:
              provider: ollama
              endpoint: http://localhost:11434
            """);

        Assert.Equal(ProviderFamilies.Ollama, local.Family);
        Assert.Null(local.Credential);
    }

    [Fact]
    public void AnOpenRouterDownstreamIsChecked() =>
        Assert.Equal(
            "openrouterProvider requires one valid downstream slug on an OpenRouter configuration",
            Refusal(
                """
                apiVersion: munarium.ioka.io/v1
                kind: ProviderConfig
                metadata:
                  name: halvard-openrouter
                spec:
                  provider: openrouter
                  openrouterProvider: "not a slug!"
                  credentialRef:
                    env: MUNARIUM_SECRET_OPENROUTER
                """));

    [Fact]
    public void ADocumentOfTheWrongKindIsRefusedRatherThanHalfUnderstood()
    {
        Assert.Equal(
            "kind must be ProviderConfig, got 'Runbook'",
            Refusal(
                """
                apiVersion: munarium.ioka.io/v1
                kind: Runbook
                metadata:
                  name: halvard-support
                spec:
                  provider: anthropic
                """));

        Assert.Equal(
            "apiVersion is required",
            Refusal(
                """
                kind: ProviderConfig
                metadata:
                  name: halvard-support
                spec:
                  provider: anthropic
                """));
    }

    [Fact]
    public void AFieldNobodyReadsIsRefusedRatherThanIgnored()
    {
        // A misspelled endpoint would otherwise silently leave the dialect's own endpoint in force.
        Assert.Equal(
            "spec: unknown field 'endpont'",
            Refusal(
                """
                apiVersion: munarium.ioka.io/v1
                kind: ProviderConfig
                metadata:
                  name: halvard-anthropic
                spec:
                  provider: anthropic
                  endpont: https://anthropic.example/v1
                  credentialRef:
                    env: MUNARIUM_SECRET_ANTHROPIC
                """));
    }

    [Fact]
    public void ADeclaredRateBudgetIsReadRatherThanRecordedAndForgotten()
    {
        // The relay enforces these, which is the difference between a ceiling and a number nobody reads.
        var declaration = Declaration(
            """
            apiVersion: munarium.ioka.io/v1
            kind: ProviderConfig
            metadata:
              name: halvard-anthropic
            spec:
              provider: anthropic
              credentialRef:
                env: MUNARIUM_SECRET_ANTHROPIC
              budgets:
                rpm: 60
                tpm: 120000
                dailyTokens:
                  capable: 5000000
            """);

        Assert.Equal(60, declaration.Budgets.RequestsPerMinute);
        Assert.Equal(120_000, declaration.Budgets.TokensPerMinute);
        Assert.Equal(5_000_000, declaration.Budgets.Capable);
        Assert.Null(declaration.Budgets.Fast);
        Assert.Null(declaration.Budgets.Frontier);
        Assert.False(declaration.Budgets.IsEmpty);
    }

    [Fact]
    public void ABudgetThatCouldNotBeEnforcedIsRefused() =>
        Assert.Equal(
            "spec.budgets.rpm must be a positive whole number",
            Refusal(
                """
                apiVersion: munarium.ioka.io/v1
                kind: ProviderConfig
                metadata:
                  name: halvard-anthropic
                spec:
                  provider: anthropic
                  credentialRef:
                    env: MUNARIUM_SECRET_ANTHROPIC
                  budgets:
                    rpm: 0
                """));

    [Fact]
    public void ACloudFamilyWithoutACredentialReferenceIsRefused() =>
        Assert.Equal(
            "credentialRef is required for this provider",
            Refusal(
                """
                apiVersion: munarium.ioka.io/v1
                kind: ProviderConfig
                metadata:
                  name: halvard-anthropic
                spec:
                  provider: anthropic
                """));

    [Fact]
    public void ACredentialReferenceIsOnePlaceAndNotTwo() =>
        Assert.Equal(
            "spec.credentialRef names both an environment variable and a file; it is one or the other",
            Refusal(
                """
                apiVersion: munarium.ioka.io/v1
                kind: ProviderConfig
                metadata:
                  name: halvard-anthropic
                spec:
                  provider: anthropic
                  credentialRef:
                    env: MUNARIUM_SECRET_ANTHROPIC
                    file: /run/secrets/anthropic
                """));

    [Fact]
    public void SomethingThatIsNotADocumentIsRefusedWithTheParsersOwnComplaint()
    {
        Assert.Equal(
            "provider config yaml: a provider config is exactly one document",
            Refusal("---\nkind: ProviderConfig\n---\nkind: ProviderConfig\n"));
    }

    [Fact]
    public void TheReservedNameIsRefusedWhereverTheDocumentIsRead() =>
        Assert.Equal(
            "the config name 'default' is reserved for the default-provider rule",
            Refusal(
                """
                apiVersion: munarium.ioka.io/v1
                kind: ProviderConfig
                metadata:
                  name: default
                spec:
                  provider: anthropic
                  credentialRef:
                    env: MUNARIUM_SECRET_ANTHROPIC
                """));

    /// <summary>Reads a document, insisting that it was read.</summary>
    /// <param name="yaml">The document.</param>
    /// <returns>The declaration.</returns>
    private static ProviderDeclaration Declaration(string yaml) => ProviderConfigReader.Read(yaml) switch
    {
        ProviderDeclaration declaration => declaration,
        ProviderConfigRefused refused => throw new InvalidOperationException($"refused: {refused.Reason}"),
    };

    /// <summary>Reads a document, insisting that it was refused, and answers with the reason.</summary>
    /// <param name="yaml">The document.</param>
    /// <returns>The reason a person would read.</returns>
    private static string Refusal(string yaml) => ProviderConfigReader.Read(yaml) switch
    {
        ProviderConfigRefused refused => refused.Reason,
        ProviderDeclaration declaration => throw new InvalidOperationException(
            $"'{declaration.Name}' was read rather than refused"),
    };
}
