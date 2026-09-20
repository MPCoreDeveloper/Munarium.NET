namespace Munarium.Runbooks.Tests;

/// <summary>Tests for materialization: answers in, documents out, and a TODO for whatever is missing.</summary>
/// <remarks>
/// The proof is the one the original makes: every emitted document is read back through the reader a deployment applies
/// it with. The assertions beyond that are over the chosen model where its names are this port's own, and over the text
/// where the point is that a guided default or a dropped area reached the document at all.
/// </remarks>
public class AuthoringMaterializerTests
{
    /// <summary>The canonical answers, as the original's own test builds them.</summary>
    private static Dictionary<string, object?> Canonical() => new(StringComparer.Ordinal)
    {
        ["identity.description"] = "Vendor security reviews for procurement.",
        ["prefix.root"] = "vendors/",
        ["prefix.areas"] = new object?[]
        {
            Area("public/", "published attestations"),
            Area("contracts/", "signed agreements"),
            Area("incidents", "incident reports"),
        },
        ["access.uniform_public"] = false,
        ["access.area_levels"] = PerArea(("public", 0L), ("contracts", 2L), ("incidents", 3L)),
        ["access.area_compartments"] = PerArea(("contracts", new object?[] { "legal" }), ("incidents", new object?[] { "security" })),
        ["extraction.media_types"] = PerArea(("contracts", new object?[] { "application/pdf" })),
        ["retrieval.candidate_n"] = 120L,
        ["completion.tier"] = "fast",
    };

    /// <summary>Answers that are complete materialize with nothing left to answer, and read back.</summary>
    [Fact]
    public void CanonicalAnswersMaterializeClean()
    {
        var (set, problem) = AuthoringMaterializer.Build(
            "vendor-security", AuthoringCatalog.Pattern("ask-the-corpus"), Canonical());

        Assert.Null(problem);
        Assert.NotNull(set);
        Assert.Empty(set.Todos);
        Assert.True(set.Documents.ContainsKey("runbooks/vendor-security.yaml"));

        var (document, refusal) = RunbookReader.Read(set.Documents["runbooks/vendor-security.yaml"]);

        Assert.Null(refusal);
        Assert.NotNull(document);
        Assert.Equal("vendor-security", document.Metadata.Name);
        Assert.Equal(1, document.Metadata.Version);

        // The area answers reached the document: three bindings, the level of the restricted one, and its tag.
        var yaml = set.Documents["runbooks/vendor-security.yaml"];
        Assert.Contains("name: vendor-security-incidents", yaml, StringComparison.Ordinal);
        Assert.Contains("accessLevel: 3", yaml, StringComparison.Ordinal);
        Assert.Contains("compartments:", yaml, StringComparison.Ordinal);
        Assert.Contains("filenamePrefix: vendors/incidents/", yaml, StringComparison.Ordinal);
    }

    /// <summary>A fresh draft is red TODOs, not a red document: placeholders still read.</summary>
    [Fact]
    public void EmptyAnswersYieldPlaceholdersAndTodos()
    {
        var (set, problem) = AuthoringMaterializer.Build("fresh", null, new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Null(problem);
        Assert.NotNull(set);
        Assert.Contains(set.Todos, todo => todo.Contains("identity.description", StringComparison.Ordinal));
        Assert.Contains(set.Todos, todo => todo.Contains("prefix.root", StringComparison.Ordinal));
        Assert.Contains(set.Todos, todo => todo.Contains("prefix.areas", StringComparison.Ordinal));
        Assert.Null(RunbookReader.Read(set.Documents["runbooks/fresh.yaml"]).Problem);
    }

    /// <summary>A pattern with no completion arm is not given one.</summary>
    [Fact]
    public void NoCompletionForPatternsWithoutOne()
    {
        var (set, _) = AuthoringMaterializer.Build("review", AuthoringCatalog.Pattern("red-flag-review"), Canonical());

        Assert.NotNull(set);
        Assert.DoesNotContain("promptTemplate", set.Documents["runbooks/review.yaml"], StringComparison.Ordinal);

        var (asked, _) = AuthoringMaterializer.Build("vendor-security", AuthoringCatalog.Pattern("ask-the-corpus"), Canonical());

        Assert.Contains("promptTemplate", asked!.Documents["runbooks/vendor-security.yaml"], StringComparison.Ordinal);
    }

    /// <summary>Supplied per-area levels are honoured, even when the uniform answer was forgotten.</summary>
    /// <remarks>
    /// The trap the original documents: levels answered and uniform left out must not be flattened to zero, because an
    /// author who supplied them would have been told nothing.
    /// </remarks>
    [Fact]
    public void SuppliedLevelsAreHonouredWithoutTheUniformAnswer()
    {
        var answers = Canonical();
        answers.Remove("access.uniform_public");

        var (set, _) = AuthoringMaterializer.Build("t", null, answers);

        Assert.NotNull(set);
        Assert.Contains("accessLevel: 3", set.Documents["runbooks/t.yaml"], StringComparison.Ordinal);

        answers["access.uniform_public"] = true;

        var (uniform, _) = AuthoringMaterializer.Build("u", null, answers);

        Assert.NotNull(uniform);
        Assert.DoesNotContain("accessLevel: 3", uniform.Documents["runbooks/u.yaml"], StringComparison.Ordinal);
    }

    /// <summary>A path that would bind the whole root is not an area.</summary>
    [Fact]
    public void RootAndEmptyAreaPathsAreDropped()
    {
        var answers = Canonical();
        answers["prefix.areas"] = new object?[] { Area("/", "the whole root is not an area"), Area("  ", ""), Area("real/", "kept") };

        var (set, _) = AuthoringMaterializer.Build("t", null, answers);

        Assert.NotNull(set);
        Assert.Contains("name: t-real", set.Documents["runbooks/t.yaml"], StringComparison.Ordinal);
        Assert.DoesNotContain("name: t-index", set.Documents["runbooks/t.yaml"], StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> Area(string path, string description) =>
        new(StringComparer.Ordinal) { ["path"] = path, ["description"] = description };

    private static Dictionary<string, object?> PerArea(params (string Area, object? Value)[] entries)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (area, value) in entries)
        {
            map[area] = value;
        }

        return map;
    }
}