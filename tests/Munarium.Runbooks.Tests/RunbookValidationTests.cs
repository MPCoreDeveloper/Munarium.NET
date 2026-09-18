namespace Munarium.Runbooks.Tests;

using Munarium.Evidence;

/// <summary>
/// The findings an operator reads before applying a runbook.
/// </summary>
/// <remarks>
/// Parsing refuses what makes a document unusable; this is the layer that reports what makes it unwise, and it is
/// deliberately a list rather than a refusal: an operator editing a runbook wants all of it at once.
/// </remarks>
public class RunbookValidationTests
{
    [Fact]
    public void TheWorkedExampleValidatesClean()
    {
        var (document, problem) = RunbookReader.Read(File.ReadAllText(
            Path.Combine(Examples(), "runbook.yaml")));

        Assert.Null(problem);
        Assert.NotNull(document);

        var findings = RunbookValidation.Validate(document);

        // Nothing to say about it: builds before the steps that depend on it, a cutover behind an approval gate, a
        // candidate count that can fill the hits it asks for, a completion inside its ranges, and four collections whose
        // access levels are not all the same.
        Assert.Empty(findings);
        Assert.True(RunbookValidation.IsValid(findings));
    }

    [Fact]
    public void ACutoverBeforeItsBuildIsAnError()
    {
        var findings = RunbookValidation.Validate(Document(new RunbookSpec
        {
            Shape = "s@1",
            Steps =
            [
                new RunbookStep { Kind = StepKind.Cutover, Approval = "required" },
                new RunbookStep { Kind = StepKind.BuildIndex },
            ],
        }));

        var finding = Assert.Single(findings, finding => finding.Code == "steps.cutover-before-build");

        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("spec.steps[0]", finding.Path);
        Assert.False(RunbookValidation.IsValid(findings));
    }

    [Fact]
    public void ACutoverWithoutAnApprovalGateIsOnlyAdvisory()
    {
        var findings = RunbookValidation.Validate(Document(new RunbookSpec
        {
            Shape = "s@1",
            Steps = [new RunbookStep { Kind = StepKind.BuildIndex }, new RunbookStep { Kind = StepKind.Cutover }],
        }));

        var finding = Assert.Single(findings, finding => finding.Code == "steps.cutover-unapproved");

        Assert.Equal(Severity.Info, finding.Severity);

        // Advisory, so the document still applies: the operator has been told, which is the whole point of a finding.
        Assert.True(RunbookValidation.IsValid(findings));
    }

    [Fact]
    public void ARetireThatKeepsNothingIsWarnedAbout()
    {
        var findings = RunbookValidation.Validate(Document(new RunbookSpec
        {
            Shape = "s@1",
            Steps =
            [
                new RunbookStep { Kind = StepKind.BuildIndex },
                new RunbookStep { Kind = StepKind.RetireOld, KeepVersions = 0 },
            ],
        }));

        var finding = Assert.Single(findings, finding => finding.Code == "steps.retire-keeps-none");

        Assert.Equal(Severity.Warn, finding.Severity);
        Assert.Equal("spec.steps[1]", finding.Path);
    }

    [Fact]
    public void ARetrievalValueOutsideItsBandIsAnError()
    {
        var findings = RunbookValidation.Validate(Document(new RunbookSpec
        {
            Shape = "s@1",
            Retrieval = new RetrievalSpec { TopK = 0, SearchConcurrency = 64 },
            Steps = [new RunbookStep { Kind = StepKind.BuildIndex }],
        }));

        Assert.Contains(findings, finding => finding.Code == "retrieval.top-k-range");
        Assert.Contains(findings, finding => finding.Code == "retrieval.search-concurrency-range");
        Assert.False(RunbookValidation.IsValid(findings));
    }

    [Fact]
    public void CandidatesBelowTheRequestedHitsAreOnlyAWarning()
    {
        // Legal, and the engine will do exactly what it says: fusion simply cannot fill the hits the document asks for.
        var findings = RunbookValidation.Validate(Document(new RunbookSpec
        {
            Shape = "s@1",
            Retrieval = new RetrievalSpec { TopK = 20, CandidateN = 10 },
            Steps = [new RunbookStep { Kind = StepKind.BuildIndex }],
        }));

        Assert.Equal(
            Severity.Warn,
            Assert.Single(findings, finding => finding.Code == "retrieval.candidates-below-top-k").Severity);
        Assert.True(RunbookValidation.IsValid(findings));
    }

    [Fact]
    public void AModelTypoIsAnErrorBecauseItWouldFallBackSilently()
    {
        var findings = RunbookValidation.Validate(Document(new RunbookSpec
        {
            Shape = "s@1",
            Models = new ModelsSpec
            {
                Tasks = new SortedDictionary<string, ModelSpec>(StringComparer.Ordinal)
                {
                    ["completions"] = new() { Tier = "turbofast" },
                },
            },
            Steps = [new RunbookStep { Kind = StepKind.BuildIndex }],
        }));

        Assert.Contains(findings, finding => finding.Code == "models.unknown-task");
        Assert.Contains(findings, finding => finding.Code == "models.bad-tier");
        Assert.False(RunbookValidation.IsValid(findings));
    }

    [Fact]
    public void ACompletionThatMissesAPlaceholderIsWarnedAbout()
    {
        var findings = RunbookValidation.Validate(Document(new RunbookSpec
        {
            Shape = "s@1",
            Completion = new CompletionSpec { PromptTemplate = "Answer: {query}" },
            Steps = [new RunbookStep { Kind = StepKind.BuildIndex }],
        }));

        var finding = Assert.Single(findings, finding => finding.Code == "completion.template-missing-var");

        Assert.Contains("{context}", finding.Message, StringComparison.Ordinal);

        // The other placeholder was there, so one finding is reported: the list is per problem, not per template.
        Assert.True(RunbookValidation.IsValid(findings));
    }

    [Fact]
    public void ACollectionBindingThatMatchesNothingIsWarnedAbout()
    {
        var findings = RunbookValidation.Validate(Document(new RunbookSpec
        {
            Collections = [new CollectionSpec { Name = "contracts", Shape = "s", Sources = new SourceBinding() }],
            Steps = [new RunbookStep { Kind = StepKind.BuildIndex }],
        }));

        // An unpinned shape and a binding that matches nothing: both legal, neither what the author meant.
        Assert.Contains(findings, finding => finding.Code == "collections.shape-unversioned");
        Assert.Contains(findings, finding => finding.Code == "collections.empty-source-binding");
        Assert.True(RunbookValidation.IsValid(findings));
    }

    private static RunbookDocument Document(RunbookSpec spec) => new()
    {
        Kind = "Runbook",
        Metadata = new RunbookMeta { Name = "test", Version = 1 },
        Spec = spec,
    };

    private static string Examples() => Path.Combine(RepositoryRoot(), "contract", "lab", "example");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "contract")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("the repository root was not found above the test binary.");
    }
}
