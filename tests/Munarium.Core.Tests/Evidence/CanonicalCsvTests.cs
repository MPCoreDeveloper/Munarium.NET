namespace Munarium.Core.Tests.Evidence;

using Munarium.Evidence;

/// <summary>
/// Tests for the canonical row form: what the row read hands back has to be exactly what was sealed, because the bytes
/// were hashed and the citation names a row number inside them.
/// </summary>
public class CanonicalCsvTests
{
    [Fact]
    public void ARowSplitsOnTheSeparator() =>
        Assert.Equal(["a", "b", "c"], CanonicalCsv.Cells("a,b,c"));

    [Fact]
    public void AnEmptyCellSurvives() => Assert.Equal(["a", "", "c"], CanonicalCsv.Cells("a,,c"));

    /// <summary>A quoted separator is one cell, not two: the difference between one counterparty and two.</summary>
    [Fact]
    public void AQuotedSeparatorIsOneCell() =>
        Assert.Equal(["a", "b,still b", "c"], CanonicalCsv.Cells("""a,"b,still b",c"""));

    [Fact]
    public void ADoubledQuoteIsOneLiteralQuote() =>
        Assert.Equal(["say \"hi\"", "z"], CanonicalCsv.Cells("\"say \"\"hi\"\"\",z"));

    /// <summary>An empty value and no value are different claims, so a trailing cell is not dropped.</summary>
    [Fact]
    public void ATrailingEmptyCellSurvives() => Assert.Equal(["a", ""], CanonicalCsv.Cells("a,"));

    [Fact]
    public void ARowWithNothingInItIsOneEmptyCell() => Assert.Equal([""], CanonicalCsv.Cells(string.Empty));
}
