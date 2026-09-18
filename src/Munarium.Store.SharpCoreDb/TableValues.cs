namespace Munarium.Store.SharpCoreDb;

using System.Globalization;

/// <summary>
/// What every table in this package has in common: how a name is spelled, how a value is read back, and which column a
/// predicate may be built over.
/// </summary>
/// <remarks>
/// The adapters keep tables of quite different shapes - source bytes, source rows, index versions, idempotency keys, and
/// the evidence plane's artifacts, grants and accesses - but how a value goes in and comes back out must not differ
/// between them, because two ways of getting a value wrong would be one way too many.
/// <para>
/// Rows go in and come out through the engine's table API rather than through SQL text, and the values are faithful
/// either way: measured on 2.1.0-RC.3, a document at <c>docs/o'brien.txt</c> is stored with its apostrophe intact. What
/// does <em>not</em> work is <em>comparing</em> caller-supplied text: a predicate over such a value - a literal or a
/// bound parameter, both measured - matches nothing, so a lookup by path would answer "no such document" for bytes that
/// are there. The predicates these tables build are therefore over identities alone (derived hashes and ULIDs, which
/// carry nothing that needs quoting), and everything else is filtered after the read.
/// </para>
/// <para>
/// No table here declares a <c>PRIMARY KEY</c>. Measured: a table with one accepted its first insert and then refused
/// every later, distinct key with "Primary key violation", so a declared key is a way to lose rows rather than a way to
/// address them. Identity is a column the adapter writes, and uniqueness is the adapter's own rule - which is what the
/// tables' own derive-and-replace behaviour already relies on.
/// </para>
/// </remarks>
internal static class TableValues
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
