namespace Munarium.Core.Tests.Runbooks;

using Munarium.Evidence;
using Munarium.Runbooks;

/// <summary>
/// The runbook document: what a v1 and a v2 document mean, and the closed vocabularies it carries.
/// </summary>
/// <remarks>
/// The reader is not exercised here - these are the document's own rules, and they hold whatever the bytes were written
/// in.
/// </remarks>
public class RunbookDocumentTests
{
    [Fact]
    public void AV2DocumentSpansItsDeclaredCollections()
    {
        var spec = new RunbookSpec
        {
            Collections = [Collection("contracts", "cuad-contracts@3"), Collection("minutes", "minutes@1")],
        };

        Assert.True(spec.IsV2);
        Assert.Equal(["contracts", "minutes"], spec.EffectiveCollections.Select(collection => collection.Name));
    }

    [Fact]
    public void AV1DocumentNormalizesToOneImplicitCollection()
    {
        var spec = new RunbookSpec { Shape = "cuad-contracts@3" };

        Assert.False(spec.IsV2);

        var collection = Assert.Single(spec.EffectiveCollections);

        // Named after the shape's name part, at level zero, with the shape carried whole. It is for display and
        // information: a v1 run stays on the shape-scoped path it has always taken, so this cannot change what it does.
        Assert.Equal("cuad-contracts", collection.Name);
        Assert.Equal("cuad-contracts@3", collection.Shape);
        Assert.Equal(0, collection.AccessLevel);
        Assert.Empty(collection.Compartments);
    }

    [Fact]
    public void ADocumentWithNeitherShapeNorCollectionsSpansNothing()
    {
        Assert.Empty(new RunbookSpec().EffectiveCollections);
    }

    [Fact]
    public void TheExecutionOrderDefaultsToStepMajor()
    {
        Assert.Equal(ExecutionOrder.StepMajor, new RunbookSpec().ExecutionOrderOf());
        Assert.Equal(ExecutionOrder.StepMajor, new RunbookSpec { Execution = new ExecutionSpec() }.ExecutionOrderOf());

        Assert.Equal(
            ExecutionOrder.CollectionMajor,
            new RunbookSpec { Execution = new ExecutionSpec { Order = ExecutionOrder.CollectionMajor } }
                .ExecutionOrderOf());
    }

    [Fact]
    public void OnlyACutoverThatAsksForItPausesTheRun()
    {
        var steps = new RunbookStep[]
        {
            new() { Kind = StepKind.ResolveSources },
            new() { Kind = StepKind.BuildIndex },
            new() { Kind = StepKind.Verify },
            new() { Kind = StepKind.VerifyDataViews },
            new() { Kind = StepKind.Cutover, Approval = "required" },
            new() { Kind = StepKind.RetireOld },
        };

        Assert.Equal(
            ["resolveSources", "buildIndex", "verify", "verifyDataViews", "cutover", "retireOld"],
            steps.Select(step => step.Name));

        // Only the cutover that declares it, and only the value the vocabulary spells: every other step runs through.
        Assert.Equal([false, false, false, false, true, false], steps.Select(step => step.RequiresApproval));

        Assert.False(new RunbookStep { Kind = StepKind.Cutover }.RequiresApproval);
        Assert.False(new RunbookStep { Kind = StepKind.Cutover, Approval = "optional" }.RequiresApproval);

        // A retire keeps two versions unless it says otherwise.
        Assert.Equal(2, new RunbookStep { Kind = StepKind.RetireOld }.KeepVersions);
    }

    [Fact]
    public void TheStepStatesAreNamedAsTheWireNamesThem()
    {
        Assert.Equal(
            ["pending", "running", "awaiting_approval", "done", "failed"],
            new[]
            {
                StepState.Pending,
                StepState.Running,
                StepState.AwaitingApproval,
                StepState.Done,
                StepState.Failed,
            }.Select(state => state.ToWireName()));
    }

    [Fact]
    public void AnOverridePolicyPermitsWhatItSaysItDoes()
    {
        // Absent means no override is permitted, which is the closed default rather than an open one.
        Assert.False(OverridePolicy.None.Permits("azure-openai"));

        Assert.True(OverridePolicy.Everything.Permits("azure-openai"));

        var allowlisted = new OverridePolicy { Allowlist = ["azure-openai"] };

        Assert.True(allowlisted.Permits("azure-openai"));
        Assert.False(allowlisted.Permits("local"));
    }

    [Fact]
    public void AnEmptyModelSpecIsEmpty()
    {
        Assert.True(new ModelSpec().IsEmpty);
        Assert.False(new ModelSpec { Tier = "capable" }.IsEmpty);
    }

    [Fact]
    public void ASourceBindingWithoutAMatcherMatchesNothing()
    {
        Assert.True(new SourceBinding().IsEmpty);
        Assert.False(new SourceBinding { FilenamePrefix = "northgate/" }.IsEmpty);
        Assert.False(new SourceBinding { ContentHashes = ["sha256:aa"] }.IsEmpty);
    }

    [Fact]
    public void ADocumentCarriesItsDeclarations()
    {
        var document = new RunbookDocument
        {
            Metadata = new RunbookMeta { Name = "northgate", Version = 2 },
            Spec = new RunbookSpec
            {
                Collections =
                [
                    Collection("contracts", "cuad-contracts@3") with
                    {
                        AccessLevel = 2,
                        Compartments = ["legal"],
                        Sources = new SourceBinding { FilenamePrefix = "northgate/" },
                        Evidence = new CollectionEvidence { Labels = ["data-room"] },
                    },
                ],
                DataViews =
                [
                    new DataViewDeclaration
                    {
                        Name = "revenue_by_region",
                        Contract = "open-pipeline-by-region@2",
                        AccessLevel = 2,
                    },
                ],
                Retrieval = new RetrievalSpec
                {
                    TopK = 20,
                    Fusion = new FusionSpec { CollectionEvidenceWeight = 0.5 },
                    ResearchProfiles =
                    [
                        new ResearchProfile
                        {
                            Name = "register",
                            Layers = [new ResearchLayer { Name = "ledger", Sources = ["facts:ver-123"] }],
                        },
                    ],
                    DefaultResearchProfile = "register",
                },
                Models = new ModelsSpec
                {
                    Default = new ModelSpec { Tier = "fast" },
                    Tasks = new SortedDictionary<string, ModelSpec>(StringComparer.Ordinal)
                    {
                        [TaskLevels.Intent] = new() { Tier = "capable" },
                    },
                    AllowOverrides = OverridePolicy.Everything,
                },
                Completion = new CompletionSpec
                {
                    PromptTemplate = "Answer from {context}: {query}",
                    Verification = new VerificationSpec { Quotes = true, Citations = true },
                    ContextCharBudget = 16_000,
                },
                Sources = new SourcesSpec { Prefix = "northgate/" },
                Execution = new ExecutionSpec { Order = ExecutionOrder.CollectionMajor },
                Steps = [new RunbookStep { Kind = StepKind.BuildIndex }],
            },
        };

        Assert.Equal("northgate", document.Metadata.Name);
        Assert.True(document.Spec.IsV2);
        Assert.Equal("northgate/", Assert.Single(document.Spec.EffectiveCollections).Sources!.FilenamePrefix);
        Assert.Equal("data-room", Assert.Single(Assert.Single(document.Spec.EffectiveCollections).Evidence!.Labels));
        Assert.Equal("revenue_by_region", Assert.Single(document.Spec.DataViews).Name);
        Assert.Equal(0.5, document.Spec.Retrieval!.Fusion!.CollectionEvidenceWeight);
        Assert.Equal("register", document.Spec.Retrieval.DefaultResearchProfile);
        Assert.Equal(ExecutionOrder.CollectionMajor, document.Spec.ExecutionOrderOf());

        // The profile a turn runs under names its memory version, which is what the fact plane then reads.
        var profile = Assert.Single(document.Spec.Retrieval.ResearchProfiles);

        Assert.Equal(["facts:ver-123"], Assert.Single(profile.Layers).Sources);
    }

    private static CollectionSpec Collection(string name, string shape) => new() { Name = name, Shape = shape };
}
