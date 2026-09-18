namespace Munarium.Evidence;

/// <summary>
/// The canonical CSV form an artifact's bytes are written in, and the only form this server reads back.
/// </summary>
/// <remarks>
/// One reader for one canonicalization, and deliberately small rather than a general CSV library: canon@1 fixes the
/// serialization, so the general problem - embedded newlines, ragged dialects, byte-order marks - is not in scope, and a
/// library that solved the general problem would also accept inputs canon@1 forbids. The bytes are hashed, so a reader
/// that accepted another dialect would be checking a different artifact than the one that was sealed.
/// </remarks>
public static class CanonicalCsv
{
    /// <summary>
    /// Splits one canonical row into its cells.
    /// </summary>
    /// <remarks>
    /// A quoted cell may hold the separator, and a doubled quote inside one is a single literal quote - which is what
    /// makes <c>"Acme, Inc"</c> one counterparty rather than two. A trailing separator produces a trailing empty cell,
    /// because an empty value and no value are different claims and this plane exists to keep them apart.
    /// </remarks>
    /// <param name="row">The row, without its newline.</param>
    /// <returns>The cells, in order.</returns>
    public static IReadOnlyList<string> Cells(string row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        var position = 0;

        while (position < row.Length)
        {
            var character = row[position];

            if (character == '"' && quoted)
            {
                // A doubled quote is one literal quote; a single one ends the cell.
                if (position + 1 < row.Length && row[position + 1] == '"')
                {
                    cell.Append('"');
                    position++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',' && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(character);
            }

            position++;
        }

        cells.Add(cell.ToString());

        return cells;
    }
}
