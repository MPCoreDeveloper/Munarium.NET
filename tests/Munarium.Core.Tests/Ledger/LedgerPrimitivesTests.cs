namespace Munarium.Core.Tests.Ledger;

using Munarium.Ledger;

/// <summary>
/// Tests for the kernel's ledger primitives.
/// </summary>
public class LedgerPrimitivesTests
{
    [Fact]
    public void StreamIdFromBlankValueThrows()
    {
        Assert.Throws<ArgumentException>(() => StreamId.From("   "));
    }

    [Fact]
    public void StreamIdFromKeepsTheValue()
    {
        var id = StreamId.From("tenant-1/ledger");

        Assert.Equal("tenant-1/ledger", id.Value);
        Assert.Equal("tenant-1/ledger", id.ToString());
    }

    [Fact]
    public void SequenceNumberZeroIsTheEmptyStreamPosition()
    {
        Assert.Equal(0, SequenceNumber.Zero.Value);
    }

    [Fact]
    public void SequenceNumberNextIncrements()
    {
        Assert.Equal(1, SequenceNumber.Zero.Next().Value);
        Assert.True(SequenceNumber.Zero.Next().CompareTo(SequenceNumber.Zero) > 0);
    }
}
