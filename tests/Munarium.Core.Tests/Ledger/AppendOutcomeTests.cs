namespace Munarium.Core.Tests.Ledger;

using System.Runtime.CompilerServices;
using Munarium.Ledger;

/// <summary>
/// Tests for the C# 15 union that models the ledger's append outcome.
/// </summary>
public class AppendOutcomeTests
{
    [Fact]
    public void EachCaseConvertsToTheUnionAndSwitchesExhaustively()
    {
        AppendOutcome appended = new Appended(new SequenceNumber(4));
        AppendOutcome conflict = new VersionConflict(new SequenceNumber(1), new SequenceNumber(4));

        Assert.Equal("appended:4", Describe(appended));
        Assert.Equal("conflict:1->4", Describe(conflict));
    }

    [Fact]
    public void TheUnionExposesItsCaseThroughIUnion()
    {
        AppendOutcome outcome = new Appended(new SequenceNumber(4));
        IUnion boxed = outcome;

        Assert.IsType<Appended>(boxed.Value);
    }

    private static string Describe(AppendOutcome outcome) => outcome switch
    {
        Appended appended => $"appended:{appended.Head.Value}",
        VersionConflict conflict => $"conflict:{conflict.Expected.Value}->{conflict.Actual.Value}",
    };
}
