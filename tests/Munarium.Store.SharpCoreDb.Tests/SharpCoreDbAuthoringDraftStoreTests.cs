namespace Munarium.Store.SharpCoreDb.Tests;

using System.Globalization;
using Munarium.Authoring;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>Tests for <see cref="SharpCoreDbAuthoringDraftStore"/>: a draft as an author leaves it.</summary>
public class SharpCoreDbAuthoringDraftStoreTests
{
    /// <summary>A draft comes back with its pattern and its answers, nested ones included.</summary>
    [Fact]
    public async Task ADraftComesBackWithItsAnswers()
    {
        await using var fixture = SourceStoreFixture.Create();

        var saved = await fixture.Drafts.SaveAsync(Draft("vendor-security"));
        var read = await fixture.Drafts.FindAsync("vendor-security");

        Assert.NotNull(read);
        Assert.Equal("ask-the-corpus", read.PatternId);
        Assert.Equal("Vendor security reviews.", read.Answers["identity.description"]);
        // Read back through the codec rather than compared as boxed values: what matters is that the answer is the
        // number the author gave, not which numeric type a round trip through text happened to produce.
        Assert.Equal(120, Convert.ToInt32(read.Answers["retrieval.candidate_n"], CultureInfo.InvariantCulture));
        Assert.Equal(false, read.Answers["access.uniform_public"]);
        Assert.Equal(saved.CreatedAt, read.CreatedAt);
        Assert.NotNull(read.UpdatedAt);

        var levels = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(read.Answers["access.area_levels"]);
        var areas = Assert.IsAssignableFrom<IReadOnlyList<object?>>(read.Answers["prefix.areas"]);
        var first = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(areas[0]);

        Assert.Equal(3L, levels["incidents"]);
        Assert.Equal("public/", first["path"]);
    }

    /// <summary>Answering again keeps the draft creation, moves its update, and creates nothing.</summary>
    [Fact]
    public async Task AnsweringAgainCreatesNothing()
    {
        await using var fixture = SourceStoreFixture.Create();

        var saved = await fixture.Drafts.SaveAsync(Draft("vendor-security"));
        var again = await fixture.Drafts.SaveAsync(Draft("vendor-security") with
        {
            Answers = new Dictionary<string, object?>(StringComparer.Ordinal) { ["prefix.root"] = "vendors/" },
        });
        var all = await fixture.Drafts.ListAsync();

        Assert.Single(all);
        Assert.Equal(saved.CreatedAt, again.CreatedAt);
        Assert.Equal("vendors/", again.Answers["prefix.root"]);
        Assert.False(again.Answers.ContainsKey("identity.description"));
    }

    /// <summary>Removing says whether there was one, and an unknown name reads as nothing.</summary>
    [Fact]
    public async Task RemovingIsHonest()
    {
        await using var fixture = SourceStoreFixture.Create();

        Assert.Null(await fixture.Drafts.FindAsync("nothing"));
        Assert.False(await fixture.Drafts.RemoveAsync("nothing"));

        await fixture.Drafts.SaveAsync(Draft("vendor-security"));

        Assert.True(await fixture.Drafts.RemoveAsync("vendor-security"));
        Assert.Null(await fixture.Drafts.FindAsync("vendor-security"));
    }

    /// <summary>A draft outlives the process that wrote it, which is the point of keeping one at all.</summary>
    [Fact]
    public async Task ADraftOutlivesTheProcessThatWroteIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");

        await using (var first = SourceStoreFixture.Create(path))
        {
            await first.Drafts.SaveAsync(Draft("vendor-security"));
        }

        await using var reopened = SourceStoreFixture.Create(path);
        var read = await reopened.Drafts.FindAsync("vendor-security");

        Assert.Equal("ask-the-corpus", read?.PatternId);
    }

    /// <summary>The draft a test writes.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The draft.</returns>
    private static AuthoringDraft Draft(string name) => new()
    {
        Name = name,
        PatternId = "ask-the-corpus",
        Answers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["identity.description"] = "Vendor security reviews.",
            ["retrieval.candidate_n"] = 120L,
            ["access.uniform_public"] = false,
            ["access.area_levels"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["public"] = 0L,
                ["contracts"] = 2L,
                ["incidents"] = 3L,
            },
            ["prefix.areas"] = new object?[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = "public/", ["description"] = "published" },
            },
        },
    };
}