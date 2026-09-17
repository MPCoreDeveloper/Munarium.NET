namespace Munarium.Text;

using System.Text;

/// <summary>
/// The Ratcliff-Obershelp similarity ratio, matching Python's
/// <c>difflib.SequenceMatcher.ratio()</c> - twice the matching characters over both lengths.
/// </summary>
/// <remarks>
/// The algorithm is the contract here, not the idea: the repetition gate and the drift engine both
/// key off this exact ratio, so a "similar" measure that scored differently would move the threshold's
/// meaning rather than the implementation. It is therefore a faithful port rather than a call into a
/// library that computes something else, and the recursion over longest matching blocks is kept
/// because that is what makes the score agree.
/// <para>
/// The comparison runs over Unicode scalar values, not UTF-16 code units: a code-unit comparison would
/// count one astral character as two and score it against two other characters.
/// </para>
/// </remarks>
public static class SimilarityRatio
{
    /// <summary>
    /// Scores two strings.
    /// </summary>
    /// <param name="left">The left string.</param>
    /// <param name="right">The right string.</param>
    /// <returns>A ratio in [0, 1]; two empty strings score 1.</returns>
    public static double Compare(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var first = Fold(left);
        var second = Fold(right);
        var total = first.Length + second.Length;

        return total == 0 ? 1.0 : (2.0 * Matching(first, second) / total);
    }

    private static Rune[] Fold(string value) =>
        [.. value.ToLowerInvariant().EnumerateRunes()];

    private static int Matching(ReadOnlySpan<Rune> left, ReadOnlySpan<Rune> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return 0;
        }

        var (leftIndex, rightIndex, length) = LongestMatch(left, right);

        return length == 0
            ? 0
            : length
                + Matching(left[..leftIndex], right[..rightIndex])
                + Matching(left[(leftIndex + length)..], right[(rightIndex + length)..]);
    }

    private static (int LeftIndex, int RightIndex, int Length) LongestMatch(
        ReadOnlySpan<Rune> left,
        ReadOnlySpan<Rune> right)
    {
        var best = (LeftIndex: 0, RightIndex: 0, Length: 0);

        // The classic dynamic-programming sweep, two rows deep: a row only ever reads the row above it.
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var i = 0; i < left.Length; i++)
        {
            for (var j = 0; j < right.Length; j++)
            {
                if (left[i] != right[j])
                {
                    continue;
                }

                var length = previous[j] + 1;
                current[j + 1] = length;

                if (length > best.Length)
                {
                    best = (i + 1 - length, j + 1 - length, length);
                }
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return best;
    }
}
