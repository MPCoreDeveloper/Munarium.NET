namespace Munarium.Runbooks.Tests;

using Munarium.Evidence;

/// <summary>
/// Reading the YAML an operator applies.
/// </summary>
/// <remarks>
/// The worked example is the original's own, byte for byte, and it is the conformance fixture - what a reader has to
/// survive is the document a deployment actually writes, not one invented here.
/// </remarks>
public class RunbookReaderTests
{
    [Fact]
    public void TheWorkedExampleReadsIntoItsDeclarations()
    {
        var (document, problem) = RunbookReader.Read(Example());

        Assert.Null(problem);
        Assert.NotNull(document);

        var spec = document.Spec;

        Assert.Equal("munarium.ioka.io/v1", document.ApiVersion);
        Assert.Equal("Runbook", document.Kind);
        Assert.Equal("halvard-support", document.Metadata.Name);
        Assert.Equal(1, document.Metadata.Version);

        // v2: four collections, three public and one behind a level and a compartment. The prefix layout IS the access
        // layout, and these bindings are what says so.
        Assert.True(spec.IsV2);
        Assert.Equal(
            ["halvard-manuals", "halvard-bulletins", "halvard-tickets", "halvard-engineering"],
            spec.EffectiveCollections.Select(collection => collection.Name));

        var engineering = spec.EffectiveCollections[3];

        Assert.Equal(1, engineering.AccessLevel);
        Assert.Equal(["engineering"], engineering.Compartments);
        Assert.Equal("halvard/engineering/", engineering.Sources!.FilenamePrefix);
        Assert.Equal(["text/markdown"], engineering.Sources.MediaTypes);

        Assert.Equal("sources", spec.Sources!.Container);
        Assert.Equal("halvard/", spec.Sources.Prefix);

        Assert.Equal(6, spec.Retrieval!.TopK);
        Assert.Equal(60, spec.Retrieval.RrfK);
        Assert.Equal(50, spec.Retrieval.CandidateN);

        // A list of provider names rather than a flag: only the lab provider may be requested.
        Assert.True(spec.Models!.AllowOverrides.Permits("lab-ollama"));
        Assert.False(spec.Models.AllowOverrides.Permits("azure-openai"));
        Assert.Equal("lab-ollama", spec.Models.Default!.Provider);
        Assert.Equal("fast", spec.Models.Tasks[TaskLevels.Completion].Tier);

        Assert.Equal(6_000, spec.Completion!.ContextCharBudget);
        Assert.Equal(1_024, spec.Completion.MaxTokens);

        // The template has to carry both placeholders, which is what makes it a RAG prompt rather than an opinion.
        Assert.Contains("{context}", spec.Completion.PromptTemplate, StringComparison.Ordinal);
        Assert.Contains("{query}", spec.Completion.PromptTemplate, StringComparison.Ordinal);

        // Five steps, in order, and the cutover is the one that pauses the run.
        Assert.Equal(
            ["resolveSources", "buildIndex", "verify", "cutover", "retireOld"],
            spec.Steps.Select(step => step.Name));
        Assert.Equal([false, false, false, true, false], spec.Steps.Select(step => step.RequiresApproval));
        Assert.Equal(2, spec.Steps[4].KeepVersions);
    }

    [Fact]
    public void EveryRuleTheOriginalRefusesAtLoadIsRefusedHere()
    {
        // The kind, so a document handed to the wrong reader is refused rather than half-understood.
        Assert.Contains(
            "kind must be Runbook",
            ProblemOf("metadata: { name: r, version: 1 }\nspec:\n  shape: s@1\n  steps:\n    - buildIndex: {}"),
            StringComparison.Ordinal);

        // A name carrying '@' would poison a runbook ref and the numeric version ordering with it.
        Assert.Contains(
            "must not contain '@'",
            ProblemOf("kind: Runbook\nmetadata: { name: r@1, version: 1 }\nspec:\n  shape: s@1\n  steps:\n    - buildIndex: {}"),
            StringComparison.Ordinal);

        // No steps: the document runs nothing.
        Assert.Contains("at least one step", ProblemOf(Document("shape: s@1\nsteps: []")), StringComparison.Ordinal);

        // Both paths at once, and neither: a document has to say which one it walks.
        Assert.Contains(
            "mutually exclusive",
            ProblemOf(Document("shape: s@1\ncollections:\n- { name: c, shape: s@1 }\nsteps:\n- buildIndex: {}")),
            StringComparison.Ordinal);

        Assert.Contains("needs spec.shape", ProblemOf(Document("steps:\n- buildIndex: {}")), StringComparison.Ordinal);

        // A semantic view with no intent task can only ever refuse at the layer with `intent-unresolved`.
        Assert.Contains(
            "needs `models.tasks.intent`",
            ProblemOf(Document(
                "collections:\n- { name: c, shape: s@1 }\n"
                    + "dataViews:\n- { name: revenue, contract: open-pipeline@2, kind: metric_view }\n"
                    + "steps:\n- buildIndex: {}")),
            StringComparison.Ordinal);

        // The one field that pauses a cutover must not fail open on a typo.
        Assert.Contains(
            "must be 'required' or 'none'",
            ProblemOf(Document("shape: s@1\nsteps:\n- cutover: { approval: Required }")),
            StringComparison.Ordinal);

        // A record without both placeholders is not refused here: that is a *finding* the validator reports, because the
        // document is still readable and an operator may be mid-edit. What is refused here is what makes it unusable.
        Assert.Contains(
            "unknown step 'reindex'",
            ProblemOf(Document("shape: s@1\nsteps:\n- reindex: {}")),
            StringComparison.Ordinal);

        // The retrieval specs deny unknown fields, so a typo cannot look like a document that said nothing.
        Assert.Contains(
            "unknown field 'topk'",
            ProblemOf(Document("shape: s@1\nretrieval: { topk: 6 }\nsteps:\n- buildIndex: {}")),
            StringComparison.Ordinal);

        // And a profile naming a source the document does not declare is refused when it is applied, not mid-turn.
        Assert.Contains(
            "not a declared collection",
            ProblemOf(Document(
                "collections:\n- { name: contracts, shape: s@1 }\n"
                    + "retrieval:\n  researchProfiles:\n  - name: register\n    layers:\n    - { name: register, sources: [nothing] }\n"
                    + "steps:\n- buildIndex: {}")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentThatIsNotYamlReportsTheParsersComplaint()
    {
        var (document, problem) = RunbookReader.Read("kind: [unclosed");

        Assert.Null(document);
        Assert.NotNull(problem);
        Assert.StartsWith("runbook yaml:", problem, StringComparison.Ordinal);
    }

    private static string ProblemOf(string yaml) =>
        RunbookReader.Read(yaml).Problem
            ?? throw new Xunit.Sdk.XunitException("the document was accepted when it should have been refused");

    private static string Document(string specBody) =>
        $"kind: Runbook\nmetadata: {{ name: r, version: 1 }}\nspec:\n{Indent(specBody)}";

    private static string Indent(string body) =>
        string.Join('\n', body.TrimEnd().Split('\n').Select(line => $"  {line}"));

    private static string Example() => File.ReadAllText(Path.Combine(Examples(), "runbook.yaml"));

    /// <summary>Finds the worked example by walking up from the test binary to the repository root.</summary>
    private static string Examples()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "contract", "lab", "example");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("contract/lab/example was not found above the test binary.");
    }
}
