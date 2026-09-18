namespace Munarium.Text;

/// <summary>
/// Deterministic chunking: a document becomes the chunks an index holds, at a version.
/// </summary>
/// <remarks>
/// Chunking is a policy rather than a detail: two chunkers over one document are two indexes, which is why the
/// version travels in the index identity next to the embedder and the extractor. This one is deliberately plain -
/// greedy, non-overlapping, breaking where the text already breaks - so a rebuild of the same document with the same
/// version produces the same chunks, and a change to the rule is a version bump rather than a quiet difference in
/// what a query matches.
/// <para>
/// Whitespace between chunks is dropped rather than carried: a chunk of newlines is not evidence, and keeping it
/// would put text into the index that says nothing. The property that matters is therefore that every
/// non-whitespace character of the document appears in exactly one chunk, in order - which is what a rebuild can be
/// checked against.
/// </para>
/// </remarks>
public static class TextChunker
{
    /// <summary>The chunking policy's version, which an index version records as identity material.</summary>
    public const string Version = "chunk@1";

    /// <summary>The break markers, in the order they are preferred.</summary>
    private static readonly string[] Breaks = ["\n\n", "\n", ". ", "! ", "? "];

    /// <summary>
    /// Splits a document into chunks of at most a size.
    /// </summary>
    /// <param name="text">The document text.</param>
    /// <param name="maxChars">The largest a chunk may be.</param>
    /// <returns>The chunks, in order, with the ordinals their identities are built from.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the size is not positive.</exception>
    public static IReadOnlyList<TextChunk> Chunk(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);

        var chunks = new List<TextChunk>();
        var index = 0;

        while (index < text.Length)
        {
            index = SkipWhitespace(text, index);

            if (index >= text.Length)
            {
                break;
            }

            var end = index + maxChars >= text.Length
                ? text.Length
                : Break(text, index, index + maxChars);

            var chunk = text[index..end].TrimEnd();

            if (chunk.Length > 0)
            {
                chunks.Add(new TextChunk(chunks.Count, chunk));
            }

            index = end;
        }

        return chunks;
    }

    // The last preferred break inside the window - paragraph, line, sentence, whitespace - or a cut that does not
    // go through a surrogate pair. `limit` is exclusive.
    private static int Break(string text, int start, int limit)
    {
        foreach (var marker in Breaks)
        {
            var at = text.LastIndexOf(marker, limit - 1, limit - start, StringComparison.Ordinal);

            if (at > start)
            {
                return at + marker.Length;
            }
        }

        for (var at = limit - 1; at > start; at--)
        {
            if (char.IsWhiteSpace(text[at]))
            {
                return at + 1;
            }
        }

        // Nothing to break on: a hard cut, but never through a surrogate pair - half a character is not text.
        return limit > start + 1 && char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }
}

/// <summary>
/// One chunk, with the ordinal its source-scoped identity is built from.
/// </summary>
/// <remarks>
/// The ordinal is the chunk's position in its source, which is how a chunk id is composed (<c>source#ordinal</c>) -
/// and why the ordinal is not the chunk's identity: the same ordinal means a different chunk after a re-chunk, and a
/// chunk id that survived that would cite text that is no longer there.
/// </remarks>
/// <param name="Ordinal">The chunk's position within its source, counting from zero.</param>
/// <param name="Text">The chunk text.</param>
public sealed record TextChunk(int Ordinal, string Text);
