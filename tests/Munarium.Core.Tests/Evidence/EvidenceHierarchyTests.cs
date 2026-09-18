namespace Munarium.Core.Tests.Evidence;

using Munarium.Core.Tests.Support;
using Munarium.Evidence;
using Munarium.Retrieval;

/// <summary>
/// Tests for the hierarchy: what a block permits, what stops a turn, and the vocabulary the wire carries.
/// </summary>
public class EvidenceHierarchyTests
{
    /// <summary>
    /// The whole of the completeness rule in one test. Document hits are the case worth stating: retrieval returns
    /// the top-k it found, never a proof that nothing else exists, so treating a good search as exhaustive is how
    /// a system says "there are no other contracts" when it means "I found three".
    /// </summary>
    [Fact]
    public void OnlyACompleteOrExactBlockSupportsACompletenessClaim()
    {
        Assert.True(Table(truncated: false).SupportsCompleteness());
        Assert.False(Table(truncated: true).SupportsCompleteness());
        Assert.True(Count().SupportsCompleteness());
        Assert.False(Hits("a").SupportsCompleteness());
        Assert.False(Slice("eyes").SupportsCompleteness());
        Assert.False(Refusal().SupportsCompleteness());
    }

    [Fact]
    public void EveryBlockReportsItsKind()
    {
        Assert.Equal("complete_table", Table(truncated: false).KindName());
        Assert.Equal("count", Count().KindName());
        Assert.Equal("fact_slice", Slice("eyes").KindName());
        Assert.Equal("document_hits", Hits("a").KindName());
        Assert.Equal("refusal", Refusal().KindName());
    }

    [Fact]
    public void AnEmptyBlockIsRecognised()
    {
        Assert.True(Hits().IsEmpty());
        Assert.True(Table(truncated: false, rows: 0).IsEmpty());
        Assert.True(Slice().IsEmpty());
        Assert.True(Refusal().IsEmpty());

        // A count is never empty: zero is a number, and an answer.
        Assert.False(Count().IsEmpty());
        Assert.False(Hits("a").IsEmpty());
    }

    [Fact]
    public void OnlyASealedBlockIsCitable()
    {
        Assert.Equal("ev-1", Table(truncated: false).EvidenceId());
        Assert.Equal("ev-1", Count().EvidenceId());
        Assert.Null(Hits("a").EvidenceId());
        Assert.Null(Slice("eyes").EvidenceId());
        Assert.Null(Refusal().EvidenceId());
    }

    /// <summary>
    /// A required layer that refused is what stops a turn: that is what <c>required</c> means. An optional layer
    /// refusing is not.
    /// </summary>
    [Fact]
    public void ARequiredLayerThatRefusedStopsTheTurn()
    {
        Assert.NotNull(Decision(LayerRequirement.Required, "refusal").RequiredLayerFailed());
        Assert.Null(Decision(LayerRequirement.Optional, "refusal").RequiredLayerFailed());
        Assert.Null(Decision(LayerRequirement.Required, "complete_table").RequiredLayerFailed());
        Assert.Null(Decision(LayerRequirement.Fallback, "refusal").RequiredLayerFailed());
    }

    [Fact]
    public void TheRoleAndRequirementNamesRoundTrip()
    {
        Assert.Equal("controlling", AnswerRole.Controlling.ToWireName());
        Assert.Equal(AnswerRole.Supporting, HierarchyNames.ParseRole("supporting"));
        Assert.Null(HierarchyNames.ParseRole("primary!"));

        Assert.Equal("fallback", LayerRequirement.Fallback.ToWireName());
        Assert.Equal(LayerRequirement.Required, HierarchyNames.ParseRequirement("required"));
        Assert.Null(HierarchyNames.ParseRequirement("mandatory"));
    }

    /// <summary>
    /// The one policy this contract version implements: a conflict between layers is preserved and disclosed,
    /// never silently resolved in favour of the higher one.
    /// </summary>
    [Fact]
    public void TheDefaultConflictPolicyIsPreserveAndDisclose() =>
        Assert.Equal(EvidencePlan.PreserveAndDisclose, Plan().Conflicts);

    private static EvidenceBlock Table(bool truncated, int rows = 1) => new TableBlock
    {
        Columns = ["region"],
        Rows = [.. Enumerable.Range(0, rows).Select(index => (IReadOnlyList<string?>)[$"r{index}"])],
        Truncated = truncated,
        EvidenceId = "ev-1",
    };

    private static EvidenceBlock Count() => new CountBlock { Value = 1204, RowsCovered = 1204, EvidenceId = "ev-1" };

    private static EvidenceBlock Hits(params string[] chunkIds) => new DocumentHits([.. chunkIds.Select(Chunk)]);

    private static EvidenceBlock Slice(params string[] keys) => new LedgerFactSlice(
    [
        .. keys.Select((key, index) => ClaimFixture.Create($"c{index}", index + 1, "hero", key, "value")),
    ]);

    private static EvidenceBlock Refusal() => new EvidenceRefusal
    {
        Code = "evidence-denied",
        Message = "the layer could not be read",
    };

    private static EvidenceHierarchyDecision Decision(LayerRequirement requirement, string block) => new()
    {
        Profile = "due-diligence",
        IntentExplicit = true,
        Layers =
        [
            new LayerOutcome
            {
                Layer = "contracts",
                Role = AnswerRole.Primary,
                Requirement = requirement,
                Block = block,
                SupportsCompleteness = block is not "refusal",
                ElapsedMilliseconds = 12,
            },
        ],
        CompletenessAvailable = block is not "refusal",
        ConflictsPolicy = EvidencePlan.PreserveAndDisclose,
    };

    private static EvidencePlan Plan() => new()
    {
        Profile = "due-diligence",
        Intent = new QueryIntent { Question = "how many contracts lapse this quarter?", Explicit = true },
        Layers =
        [
            new EvidenceLayer
            {
                Name = "contracts",
                Sources = ["contracts-view"],
                Requirement = LayerRequirement.Required,
                Role = AnswerRole.Primary,
                PreserveCompleteResult = true,
            },
        ],
    };

    private static RetrievedChunk Chunk(string chunkId) => new(
        new SourceReference(chunkId, "source-1", "docs/policy.pdf", "sha256:abc", ChunkOrdinal: 0),
        Score: 0,
        $"text of {chunkId}");
}
