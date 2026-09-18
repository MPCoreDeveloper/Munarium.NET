namespace Munarium.Core.Tests.Evidence;

using Munarium.Evidence;
using static Munarium.Core.Tests.Support.HierarchyFixture;

/// <summary>
/// Tests for composition: what fits, what is dropped whole, and the row ids a citation has to resolve against.
/// </summary>
public class HierarchyComposerTests
{
    /// <summary>
    /// Whole or nothing. Half a table is not a smaller true answer but a false one, and a model shown nine of
    /// twelve rows will answer about twelve.
    /// </summary>
    [Fact]
    public void APreservedLayerIsTakenWholeOrNotAtAll()
    {
        var plan = Plan(Layer("billing", "matrix:billing", LayerRequirement.Required, AnswerRole.Primary, preserve: true));
        var blocks = new LayerBlock[] { new("billing", Table(truncated: false)) };

        var roomy = HierarchyComposer.Compose(plan, blocks, budget: 1_000);
        Assert.Equal(1, roomy.LayersUsed);
        Assert.Empty(roomy.LayersDropped);
        Assert.Contains("COMPLETE", roomy.Context, StringComparison.Ordinal);

        var cramped = HierarchyComposer.Compose(plan, blocks, budget: 10);
        Assert.Equal(0, cramped.LayersUsed);
        Assert.Equal(["billing"], cramped.LayersDropped);
        Assert.Empty(cramped.Context);
    }

    [Fact]
    public void ALayerThatMayBeCutIsTruncatedToFit()
    {
        var plan = Plan(Layer("notes", "notes", LayerRequirement.Optional, AnswerRole.Supporting));
        var blocks = new LayerBlock[] { new("notes", Table(truncated: false)) };

        var composed = HierarchyComposer.Compose(plan, blocks, budget: 20);
        var whole = HierarchyComposer.Compose(plan, blocks, budget: 1_000).Context;

        Assert.Equal(1, composed.LayersUsed);
        Assert.Equal(20, composed.Context.Length);

        // What the model is given is a prefix of the whole: cutting never rearranges or rewrites evidence.
        Assert.StartsWith(composed.Context, whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// A per-layer budget that only the grammar validated and nobody read would let a supporting layer declared at
    /// four thousand characters consume the whole profile budget.
    /// </summary>
    [Fact]
    public void APerLayerBudgetCapsWhatALayerConsumes()
    {
        var plan = Plan(
            Layer("invoices", "matrix:invoices", LayerRequirement.Optional, AnswerRole.Primary, charBudget: 8),
            Layer("payments", "matrix:payments", LayerRequirement.Optional, AnswerRole.Primary));

        var composed = HierarchyComposer.Compose(
            plan,
            [
                new LayerBlock("invoices", Table(truncated: false)),
                new LayerBlock("payments", Slice("owner_team")),
            ],
            budget: 1_000);

        Assert.Equal(2, composed.LayersUsed);
        Assert.Contains("[payments] recorded facts", composed.Context, StringComparison.Ordinal);
        Assert.True(composed.Context.Length < 200, "the first layer's own cap had to hold it back");
    }

    [Fact]
    public void ABudgetOfZeroDropsEveryLayer()
    {
        var plan = Plan(Layer("contracts", "contracts", LayerRequirement.Optional, AnswerRole.Primary));

        var composed = HierarchyComposer.Compose(
            plan,
            [new LayerBlock("contracts", Table(truncated: false))],
            budget: 0);

        Assert.Empty(composed.Context);
        Assert.Equal(["contracts"], composed.LayersDropped);
    }

    /// <summary>
    /// A model that cannot tell a complete table from a truncated one will treat both as complete, and a cell with
    /// no value is not the same fact as an empty string.
    /// </summary>
    [Fact]
    public void TheContextSaysWhetherATableIsCompleteAndSpellsOutNullCells()
    {
        var plan = Plan(Layer("billing", "matrix:billing", LayerRequirement.Optional, AnswerRole.Primary));

        var composed = HierarchyComposer.Compose(
            plan,
            [new LayerBlock("billing", Table(truncated: true, rows: [["EMEA", null], ["APAC", string.Empty]]))],
            budget: 1_000);

        Assert.Contains("[billing] TRUNCATED result (2 rows), columns: region", composed.Context, StringComparison.Ordinal);
        Assert.Contains($"r0001 | EMEA | {HierarchyComposer.NullCell}", composed.Context, StringComparison.Ordinal);
        Assert.Contains("r0002 | APAC | \n", composed.Context, StringComparison.Ordinal);
        Assert.Contains("evidence: ev-1", composed.Context, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal is disclosed to the model, so an answer can say the register was never consulted - but the
    /// refusal's source is not, because a caller who cannot see a source must not learn of it from the context.
    /// </summary>
    [Fact]
    public void ARefusalIsDisclosedWithoutItsSource()
    {
        var plan = Plan(Layer("register", "matrix:register", LayerRequirement.Optional, AnswerRole.Supporting));

        var composed = HierarchyComposer.Compose(
            plan,
            [
                new LayerBlock("register", new EvidenceRefusal
                {
                    Code = EvidenceRefusalCodes.SourceNotBound,
                    Message = "no provider is configured for this layer's sources",
                    Source = "matrix:register",
                }),
            ],
            budget: 1_000);

        Assert.Contains(
            "[register] no evidence available (source-not-bound): no provider is configured",
            composed.Context,
            StringComparison.Ordinal);
        Assert.DoesNotContain("matrix:register", composed.Context, StringComparison.Ordinal);
    }

    /// <summary>
    /// The citation rule: the model cites the id it was shown and a checker resolves the id it was given, so the
    /// served rows are numbered by the same function that rendered them.
    /// </summary>
    [Fact]
    public void TheServedRowsAreNumberedExactlyAsTheContextShowsThem()
    {
        var plan = Plan(Layer("billing", "matrix:billing", LayerRequirement.Optional, AnswerRole.Primary));
        var blocks = new LayerBlock[]
        {
            new("billing", Table(truncated: false, rows: [["EMEA", "12"], ["APAC", "7"]], rowIds: ["EMEA", "APAC"])),
            new("loose", Table(truncated: false, evidenceId: null)),
        };

        var composed = HierarchyComposer.Compose(plan, blocks, budget: 1_000);
        var served = Assert.Single(HierarchyComposer.ServedEvidence(blocks));

        Assert.Equal("ev-1", served.EvidenceId);
        Assert.Equal(["EMEA", "APAC"], served.Rows.Select(row => row.RowId));

        foreach (var row in served.Rows)
        {
            Assert.Contains(
                $"{row.RowId} | {string.Join(" | ", row.Cells)}",
                composed.Context,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ATableWithoutASealedIdentityIsNotCitable() =>
        Assert.Empty(HierarchyComposer.ServedEvidence(
            [new LayerBlock("loose", Table(truncated: false, evidenceId: null))]));

    [Fact]
    public void AFactSliceIsRenderedAsItsClaimKeyAndValue()
    {
        var plan = Plan(Layer("ledger", "facts:release-1", LayerRequirement.Optional, AnswerRole.Supporting));

        var composed = HierarchyComposer.Compose(
            plan,
            [new LayerBlock("ledger", Slice("owner_team"))],
            budget: 1_000);

        Assert.Contains("[ledger] recorded facts", composed.Context, StringComparison.Ordinal);
        Assert.Contains("service.owner_team = v0", composed.Context, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowIdFallsBackToItsPositionWhenTheSealerNamedNone()
    {
        var named = AsTable(Table(truncated: false, rowIds: ["EMEA", "APAC"]));

        Assert.Equal("r0001", HierarchyComposer.TableRowId(AsTable(Table(truncated: false)), 0));
        Assert.Equal("EMEA", HierarchyComposer.TableRowId(named, 0));
        Assert.Equal("APAC", HierarchyComposer.TableRowId(named, 1));
    }

    private static TableBlock AsTable(EvidenceBlock block) =>
        block is TableBlock table
            ? table
            : throw new InvalidOperationException("The fixture did not build a table.");
}
