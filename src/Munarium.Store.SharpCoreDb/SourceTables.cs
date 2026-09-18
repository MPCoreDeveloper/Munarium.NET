namespace Munarium.Store.SharpCoreDb;

using System.Globalization;

/// <summary>
/// What the two source tables have in common: how they are named and how their rows are read.
/// </summary>
/// <remarks>
/// Two tables rather than one, because bytes and rows change at different rates - bytes are written once and read
/// often, a row is rewritten on every ingest - but one way of doing it, because two ways of getting a value wrong would
/// be one way too many.
/// <para>
/// They are written through the engine's table API rather than through SQL text, which is a decision made from
/// measurement rather than taste: a value containing a quote does not survive the SQL literal parser, so a document at
/// <c>docs/o'brien.txt</c> would be stored as <c>docs/obrien.txt</c> - a citation pointing at a document that does not
/// exist. Rows go in as rows, and the only predicates ever built are over identities, which are derived hashes and
/// therefore contain nothing that needs quoting.
/// </para>
/// <para>
/// The consequence is that everything else is filtered after the rows are read: a caller-supplied path can carry a
/// quote, a percent sign or anything else, and a row that is <em>found</em> has to be the row that was <em>written</em>.
/// At this port's scale - one tenant's sources, read as a set - that is the honest trade.
/// </para>
/// </remarks>
internal static class SourceTables
{
    /// <summary>Validates a table name, which is the one part of a statement that cannot be a parameter.</summary>
    /// <param name="tableName">The table name.</param>
    /// <returns>The name, when it is a bare identifier.</returns>
    /// <exception cref="ArgumentException">Thrown when the name is not a bare identifier.</exception>
    public static string ValidateTableName(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        return tableName.Any(character => !char.IsLetterOrDigit(character) && character != '_')
            ? throw new ArgumentException(
                $"table name '{tableName}' is not a bare identifier; letters, digits and underscores only",
                nameof(tableName))
            : tableName;
    }

    /// <summary>Builds the predicate that selects one identity, which is a derived hash and so needs no quoting.</summary>
    /// <param name="column">The identity column.</param>
    /// <param name="value">The identity.</param>
    /// <returns>The predicate.</returns>
    public static string Identity(string column, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return $"{column} = '{value}'";
    }

    /// <summary>Reads a string column, answering empty for a missing or null one.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The value.</returns>
    public static string StringValue(Dictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    /// <summary>Reads a numeric column, answering zero for a missing or unreadable one.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The value.</returns>
    public static long LongValue(Dictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) && value is not null
            && long.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
            ? parsed
            : 0L;
}
