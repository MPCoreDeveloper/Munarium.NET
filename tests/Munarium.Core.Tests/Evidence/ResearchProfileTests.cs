namespace Munarium.Core.Tests.Evidence;

using Munarium.Evidence;

/// <summary>
/// The declarative half of the hierarchy: what a profile may say, and what is refused when it says it wrong.
/// </summary>
/// <remarks>
/// The cases are the original's own, including the ones that look pedantic. Every check is one that would otherwise fire
/// mid-turn, in front of a user, with money already spent - so the moment to find out is when the profile is applied.
/// </remarks>
public class ResearchProfileTests
{
    [Fact]
    public void AnUnknownSourceIsRefusedAtApply()
    {
        var problem = ResearchValidation.Validate(
            [Profile("d", Layer("l", "nonexistent"))], [], null, Collections, null);

        Assert.NotNull(problem);
        Assert.Equal("nonexistent", problem.Source);
        Assert.Equal("l", problem.Layer);
    }

    [Fact]
    public void AnUnknownDataViewIsRefusedEvenThoughThePrefixIsRight()
    {
        // Naming `matrix:` is not enough: a view the document does not declare is not a view, and accepting the prefix
        // alone would make every mistyped view a refusal at turn time instead of an error at apply time.
        var problem = ResearchValidation.Validate(
            [Profile("d", Layer("l", "matrix:not_declared"))], [View("revenue")], null, Collections, null);

        Assert.NotNull(problem);
        Assert.Equal("matrix:not_declared", problem.Source);
    }

    [Fact]
    public void ARequiredWholeTableThatCannotFitTheBudgetIsAContradiction()
    {
        // The sharpest check: this profile would refuse EVERY turn. Finding that out one turn at a time, after paying
        // for each, is the failure mode the check exists to prevent.
        var layer = Layer("register", "matrix:revenue") with
        {
            Requirement = LayerRequirement.Required,
            PreserveCompleteResult = true,
            MaxBytes = 64_000,
        };

        var problem = ResearchValidation.Validate(
            [Profile("d", layer)], [View("revenue")], null, Collections, 16_000);

        Assert.NotNull(problem);
        Assert.Equal(64_000, problem.MaxBytes);
        Assert.Equal(16_000, problem.Budget);
        Assert.Contains("every turn using this profile would refuse", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameLayerOptionalIsAllowed()
    {
        // Optional and whole-or-nothing is a legitimate design: it contributes when it fits and stays silent when it
        // does not. Only `required` turns a too-large table into a guaranteed refusal.
        var layer = Layer("register", "matrix:revenue") with
        {
            Requirement = LayerRequirement.Optional,
            PreserveCompleteResult = true,
            MaxBytes = 64_000,
        };

        Assert.Null(ResearchValidation.Validate(
            [Profile("d", layer)], [View("revenue")], null, Collections, 16_000));
    }

    [Fact]
    public void ALayerBudgetOverridesTheCompletionBudget()
    {
        var layer = Layer("register", "matrix:revenue") with
        {
            Requirement = LayerRequirement.Required,
            PreserveCompleteResult = true,
            MaxBytes = 64_000,
            ContextCharBudget = 100_000,
        };

        Assert.Null(ResearchValidation.Validate(
            [Profile("d", layer)], [View("revenue")], null, Collections, 16_000));
    }

    [Fact]
    public void PreserveWithoutMaxBytesIsRefusedBecauseItCannotBeChecked()
    {
        var layer = Layer("register", "matrix:revenue") with { PreserveCompleteResult = true };

        var problem = ResearchValidation.Validate(
            [Profile("d", layer)], [View("revenue")], null, Collections, 16_000);

        Assert.NotNull(problem);
        Assert.Contains("no maxBytes", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAllFallbackProfileNeverRunsAnything()
    {
        // A fallback runs only when something before it produced nothing, and nothing before it exists.
        var layer = Layer("l", "contracts") with { Requirement = LayerRequirement.Fallback };

        var problem = ResearchValidation.Validate([Profile("d", layer)], [], null, Collections, null);

        Assert.NotNull(problem);
        Assert.Contains("only fallback layers", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourcelessLayerIsRefused()
    {
        var problem = ResearchValidation.Validate([Profile("d", Layer("l"))], [], null, Collections, null);

        Assert.NotNull(problem);
        Assert.Contains("names no sources", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatesAndADanglingDefaultAreAllRefused()
    {
        var duplicateProfile = ResearchValidation.Validate(
            [Profile("d", Layer("l", "contracts")), Profile("d", Layer("l", "contracts"))],
            [],
            null,
            Collections,
            null);

        Assert.Contains("duplicate research profile", duplicateProfile!.Detail, StringComparison.Ordinal);

        var duplicateLayer = ResearchValidation.Validate(
            [Profile("d", Layer("l", "contracts"), Layer("l", "minutes"))], [], null, Collections, null);

        Assert.Contains("declares layer 'l' twice", duplicateLayer!.Detail, StringComparison.Ordinal);

        var duplicateView = ResearchValidation.Validate([], [View("v"), View("v")], null, Collections, null);

        Assert.Contains("duplicate data view", duplicateView!.Detail, StringComparison.Ordinal);

        var danglingDefault = ResearchValidation.Validate(
            [Profile("d", Layer("l", "contracts"))], [], "missing", Collections, null);

        Assert.Contains("is not a declared profile", danglingDefault!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AColonInANameIsRefusedBecauseItCollidesWithTheSourcePrefix()
    {
        // `matrix:x` is how a layer names a data view; a view literally named `matrix:x` would make source resolution
        // ambiguous, and one of the two readings would silently win.
        var problem = ResearchValidation.Validate(
            [Profile("bad:name", Layer("l", "contracts"))], [], null, Collections, null);

        Assert.NotNull(problem);
        Assert.Contains("must not contain whitespace or ':'", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AFactLayerNamingItsVersionResolves()
    {
        Assert.Null(ResearchValidation.Validate(
            [Profile("d", Layer("l", "facts:ver-123", "scope:northgate/contracts"))],
            [],
            null,
            Collections,
            null));
    }

    [Fact]
    public void ABareFactsLayerIsRefusedBecauseItCouldOnlyEverRefuse()
    {
        // Sessions carry no memory-version binding, so `facts` with no version names nothing readable. Accepting it
        // would ship a runbook that validates and then refuses every turn.
        var problem = ResearchValidation.Validate([Profile("d", Layer("l", "facts"))], [], null, Collections, null);

        Assert.NotNull(problem);
        Assert.Equal("facts", problem.Source);
    }

    [Fact]
    public void AProfileResolvesToAPlanWhoseLayerOrderIsTheHierarchy()
    {
        var profile = Profile(
            "register",
            Layer("ledger", "facts:ver-123") with
            {
                Requirement = LayerRequirement.Required,
                Role = AnswerRole.Controlling,
            },
            Layer("documents", "contracts") with { ContextCharBudget = 4_000 }) with { ContextCharBudget = 12_000 };

        var plan = ResearchProfiles.BuildPlan(profile, new QueryIntent { Question = "q" });

        Assert.Equal("register", plan.Profile);
        Assert.Equal(12_000, plan.ContextCharBudget);

        // One policy this contract implements, and the field exists so an alternative would be a visible change.
        Assert.Equal(EvidencePlan.PreserveAndDisclose, plan.Conflicts);

        Assert.Equal(["ledger", "documents"], plan.Layers.Select(layer => layer.Name));
        Assert.Equal(AnswerRole.Controlling, plan.Layers[0].Role);
        Assert.Equal(LayerRequirement.Required, plan.Layers[0].Requirement);
        Assert.Equal(4_000, plan.Layers[1].ContextCharBudget);

        // The sources are the profile's own, pinned when it was applied and never resolved during a turn.
        Assert.Equal(["facts:ver-123"], plan.Layers[0].Sources);
    }

    [Fact]
    public void ANamedButUndeclaredProfileFailsClosed()
    {
        // Silently falling back to the document path would answer a different question than the caller asked, and would
        // do it invisibly.
        var (profile, problem) = ResearchProfiles.Resolve(
            [Profile("d", Layer("l", "contracts"))], "missing", null);

        Assert.Null(profile);
        Assert.NotNull(problem);
        Assert.Contains("unknown research profile 'missing'", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRequestAndNoDefaultMeansTheDocumentPath()
    {
        var (profile, problem) = ResearchProfiles.Resolve(
            [Profile("d", Layer("l", "contracts"))], null, null);

        Assert.Null(profile);
        Assert.Null(problem);
    }

    [Fact]
    public void TheDefaultProfileIsUsedWhenATurnNamesNone()
    {
        var declared = Profile("house", Layer("l", "contracts"));

        var (profile, problem) = ResearchProfiles.Resolve([declared], null, "house");

        Assert.Null(problem);
        Assert.Same(declared, profile);
    }

    private static string[] Collections => ["contracts", "minutes"];

    private static ResearchLayer Layer(string name, params string[] sources) =>
        new() { Name = name, Sources = sources };

    private static ResearchProfile Profile(string name, params ResearchLayer[] layers) =>
        new() { Name = name, Layers = layers };

    private static DataViewDeclaration View(string name) =>
        new() { Name = name, Contract = $"{name}@1" };
}
