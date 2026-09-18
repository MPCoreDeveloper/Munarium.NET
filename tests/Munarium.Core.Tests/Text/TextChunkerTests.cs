namespace Munarium.Core.Tests.Text;

using Munarium.Text;

/// <summary>
/// Tests for chunking: the size is a ceiling, the breaks follow the text, and a rebuild of one document with one
/// version produces the same chunks.
/// </summary>
public class TextChunkerTests
{
    [Fact]
    public void NoChunkIsLargerThanTheCeilingAndNoneIsEmpty()
    {
        var chunks = TextChunker.Chunk(Paragraphs, maxChars: 40);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 40));

        // The ordinals are the positions a chunk id is composed from, so they have to be dense and in order.
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(chunk => chunk.Ordinal));
    }

    /// <summary>
    /// Whitespace between chunks is dropped, so the property a rebuild can be checked against is that every
    /// non-whitespace character is still there, in order, exactly once.
    /// </summary>
    [Fact]
    public void NoTextIsLostOrRepeated()
    {
        var chunks = TextChunker.Chunk(Paragraphs, maxChars: 30);
        var rebuilt = string.Concat(chunks.Select(chunk => chunk.Text));

        Assert.Equal(Compact(Paragraphs), Compact(rebuilt));
    }

    [Fact]
    public void TheSameDocumentAndVersionChunkTheSameWay()
    {
        var first = TextChunker.Chunk(Paragraphs, maxChars: 64);
        var second = TextChunker.Chunk(Paragraphs, maxChars: 64);

        Assert.Equal(first, second);
        Assert.Equal("chunk@1", TextChunker.Version);
    }

    /// <summary>
    /// The break follows the text rather than the ceiling: a paragraph break inside the window is taken, which is
    /// what makes a chunk a readable unit rather than a 40-character slice.
    /// </summary>
    [Fact]
    public void ABreakInsideTheWindowIsPreferredToTheCeiling()
    {
        var chunks = TextChunker.Chunk("first paragraph.\n\nsecond paragraph.", maxChars: 30);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("first paragraph.", chunks[0].Text);
        Assert.Equal("second paragraph.", chunks[1].Text);
    }

    [Fact]
    public void ABreakThatDoesNotFitIsNotTakenBeyondTheCeiling()
    {
        var chunks = TextChunker.Chunk("a long sentence that goes on and on and on and on.", maxChars: 20);

        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= 20));
        Assert.True(chunks.Count > 1);
    }

    /// <summary>Half a character is not text, so a cut that would land inside a pair moves back one.</summary>
    [Fact]
    public void AHardCutDoesNotSplitASurrogatePair()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F600", 10));

        var chunks = TextChunker.Chunk(text, maxChars: 5);

        // A chunk may contain surrogate pairs - it must simply not begin or end with half of one.
        Assert.All(chunks, chunk =>
        {
            Assert.False(char.IsHighSurrogate(chunk.Text[^1]), "a chunk must not end in half a character");
            Assert.False(char.IsLowSurrogate(chunk.Text[0]), "a chunk must not start in half a character");
        });

        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.Text)));
    }

    [Fact]
    public void ADocumentWithNothingToIndexGetsNoChunks()
    {
        Assert.Empty(TextChunker.Chunk(string.Empty, maxChars: 10));
        Assert.Empty(TextChunker.Chunk("   \n\n\t ", maxChars: 10));
    }

    [Fact]
    public void ASizeThatIsNotPositiveIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextChunker.Chunk("text", maxChars: 0));
        Assert.Throws<ArgumentNullException>(() => TextChunker.Chunk(null!, maxChars: 10));
    }

    private const string Paragraphs =
        "The Bell rang twice.\n\n" +
        "A second paragraph, longer than the first one, with a sentence in it.\n" +
        "And a third line that belongs to the same paragraph.\n\n" +
        "Finally a closing paragraph.";

    private static string Compact(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
}
