namespace Munarium.Evidence;

/// <summary>
/// What the sealed bytes are.
/// </summary>
/// <remarks>
/// <em>Closed</em>: a new member is a major contract bump, because a reader that does not recognize a kind
/// cannot say what it is holding.
/// </remarks>
public enum EvidenceKind
{
    /// <summary>A query result table.</summary>
    Table = 0,

    /// <summary>An exact count with its coverage.</summary>
    Count = 1,

    /// <summary>A batch of typed observations.</summary>
    Observations = 2,
}

/// <summary>
/// Logical column types.
/// </summary>
/// <remarks>
/// <em>Closed</em>, and deliberately logical rather than physical: the canonical scalar encodings are defined
/// exactly over this set, so a type that only exists in one engine's type system would have no encoding.
/// </remarks>
public enum ColumnType
{
    /// <summary>A boolean.</summary>
    Bool = 0,

    /// <summary>A 64-bit signed integer.</summary>
    WholeNumber = 1,

    /// <summary>A decimal with a declared scale.</summary>
    ExactDecimal = 2,

    /// <summary>A 64-bit float.</summary>
    RealNumber = 3,

    /// <summary>Text.</summary>
    Text = 4,

    /// <summary>Opaque bytes.</summary>
    Bytes = 5,

    /// <summary>A calendar date.</summary>
    Date = 6,

    /// <summary>A timestamp with a zone.</summary>
    TimestampTz = 7,

    /// <summary>A timestamp without a zone.</summary>
    TimestampNaive = 8,

    /// <summary>A duration.</summary>
    Interval = 9,

    /// <summary>A UUID.</summary>
    Uuid = 10,

    /// <summary>Structured JSON.</summary>
    Json = 11,

    /// <summary>An array, whose element type is declared separately.</summary>
    Array = 12,
}

/// <summary>
/// How rows are identified - the canonical ordering rule for a result.
/// </summary>
public enum RowIdRule
{
    /// <summary>A row id derives from the declared key tuple, so the result hashes as a multiset and order is irrelevant.</summary>
    Keys = 0,

    /// <summary>A row id is the position, which is legal only under a total ordering.</summary>
    Position = 1,
}

/// <summary>
/// One column of a sealed result.
/// </summary>
/// <remarks>
/// The identity and the name are separate because a source may rename a column without changing what it means:
/// a consumer that keyed on the name would see a new column where nothing changed.
/// </remarks>
public sealed record EvidenceColumn
{
    /// <summary>Gets the stable identity, which survives a rename of the source column.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the column's name as the source reports it.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the logical type.</summary>
    public required ColumnType Type { get; init; }

    /// <summary>Gets a value indicating whether the column may hold no value.</summary>
    public bool Nullable { get; init; }

    /// <summary>Gets the decimal scale, when the type has one.</summary>
    public int? Scale { get; init; }

    /// <summary>Gets the unit, such as <c>USD</c> or <c>seconds</c>, carried so a number is never unitless.</summary>
    public string? Unit { get; init; }

    /// <summary>Gets whether the column may be summed, when the producer says so.</summary>
    public string? Additivity { get; init; }

    /// <summary>Gets a value indicating whether the column is part of the row identity.</summary>
    public bool Key { get; init; }

    /// <summary>Gets the element type of an array column.</summary>
    public string? ElementType { get; init; }
}

/// <summary>
/// The schema of a sealed result.
/// </summary>
/// <param name="Columns">The columns, in result order.</param>
public sealed record EvidenceSchema(IReadOnlyList<EvidenceColumn> Columns);

/// <summary>
/// What makes two rows of a result the same row, and how many there were.
/// </summary>
/// <param name="RowIdRule">How a row is identified.</param>
public sealed record EvidenceIdentity(RowIdRule RowIdRule)
{
    /// <summary>Gets the declared ordering, which a positional row rule requires to be total.</summary>
    public IReadOnlyList<string> OrderBy { get; init; } = [];

    /// <summary>Gets how many rows the result held, when the producer says so.</summary>
    public long? Rows { get; init; }
}

/// <summary>
/// Whether a result is complete, and what was left out.
/// </summary>
/// <remarks>
/// A truncated block cannot support a completeness claim, and the server enforces that; this is where it
/// learns what the producer means by complete.
/// </remarks>
public sealed record Completeness
{
    /// <summary>Gets a value indicating whether rows were cut off.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Gets the row ceiling the producer declared, when it declared one.</summary>
    public long? DeclaredMaxRows { get; init; }

    /// <summary>Gets how many source rows the run covered.</summary>
    public long? RowsCovered { get; init; }

    /// <summary>Gets how many rows policy or drift excluded, which are reported and never silently dropped.</summary>
    public long? RowsExcluded { get; init; }

    /// <summary>Gets why rows were excluded.</summary>
    public string? ExclusionReason { get; init; }
}

/// <summary>
/// What a policy removed before the bytes were written.
/// </summary>
/// <remarks>
/// A denied column was never selected, so it is absent from the bytes rather than blank; naming it here is how
/// an operator sees why a column is missing instead of inferring it from a gap.
/// </remarks>
public sealed record Redaction
{
    /// <summary>Gets the columns the policy denied.</summary>
    public IReadOnlyList<string> DeniedColumns { get; init; } = [];

    /// <summary>Gets a value indicating whether values were masked rather than removed.</summary>
    public bool Masked { get; init; }
}

/// <summary>
/// Which source a result came from, and what executed the query.
/// </summary>
/// <param name="SourceId">The source's identity.</param>
/// <param name="SourceVersion">The source's version at the time.</param>
/// <param name="Adapter">The adapter, as an open vocabulary.</param>
public sealed record SourceRef(string SourceId, long SourceVersion, string Adapter)
{
    /// <summary>Gets the adapter's version.</summary>
    public string? AdapterVersion { get; init; }

    /// <summary>Gets what executed the query, as the engine reports itself; provenance, not identity.</summary>
    public string? Engine { get; init; }

    /// <summary>Gets the driver the engine used.</summary>
    public string? Driver { get; init; }
}

/// <summary>
/// Every versioned artifact that shaped a result.
/// </summary>
/// <remarks>
/// Absent means "not applicable to this kind", never "unknown": the difference matters because a reader
/// deciding whether to trust a result needs to know which of the two it is looking at.
/// </remarks>
public sealed record Versions
{
    /// <summary>Gets the query contract version.</summary>
    public string? QueryContract { get; init; }

    /// <summary>Gets the claim-mapping version.</summary>
    public string? ClaimMapping { get; init; }

    /// <summary>Gets the semantic-provider version.</summary>
    public string? SemanticProvider { get; init; }

    /// <summary>Gets the render version.</summary>
    public string? Render { get; init; }

    /// <summary>Gets the policy version.</summary>
    public string? Policy { get; init; }

    /// <summary>Gets the compiler version.</summary>
    public string? Compiler { get; init; }
}

/// <summary>
/// Hashes of the compiled plan and the bound parameters.
/// </summary>
/// <remarks>
/// The statement text is never sealed and never leaves the engine: a plan hash proves which query ran without
/// carrying the query.
/// </remarks>
public sealed record PlanHashes
{
    /// <summary>Gets the canonical plan hash.</summary>
    public string? CanonicalPlanHash { get; init; }

    /// <summary>Gets the hash of the bound parameters.</summary>
    public string? BoundParametersHash { get; init; }
}

/// <summary>
/// The contract spellings of <see cref="ColumnType"/>.
/// </summary>
/// <remarks>
/// The member names are the port's; this is the contract's. Keeping the mapping explicit rather than relying
/// on an enum's member names is what makes the wire vocabulary one auditable table - and it frees a member to
/// be named for a reader of C# rather than for a JSON parser.
/// </remarks>
public static class ColumnTypeNames
{
    /// <summary>Returns the name the contract uses for a column type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The contract spelling.</returns>
    public static string ToContractName(this ColumnType type) => type switch
    {
        ColumnType.Bool => "bool",
        ColumnType.WholeNumber => "int64",
        ColumnType.ExactDecimal => "decimal",
        ColumnType.RealNumber => "float64",
        ColumnType.Text => "string",
        ColumnType.Bytes => "bytes",
        ColumnType.Date => "date",
        ColumnType.TimestampTz => "timestamp_tz",
        ColumnType.TimestampNaive => "timestamp_naive",
        ColumnType.Interval => "interval",
        ColumnType.Uuid => "uuid",
        ColumnType.Json => "json",
        _ => "array",
    };

    /// <summary>
    /// Parses a contract name.
    /// </summary>
    /// <param name="name">The contract spelling.</param>
    /// <returns>The type, or <see langword="null"/> when the name is not one of the closed set.</returns>
    public static ColumnType? ParseType(string? name) => name switch
    {
        "bool" => ColumnType.Bool,
        "int64" => ColumnType.WholeNumber,
        "decimal" => ColumnType.ExactDecimal,
        "float64" => ColumnType.RealNumber,
        "string" => ColumnType.Text,
        "bytes" => ColumnType.Bytes,
        "date" => ColumnType.Date,
        "timestamp_tz" => ColumnType.TimestampTz,
        "timestamp_naive" => ColumnType.TimestampNaive,
        "interval" => ColumnType.Interval,
        "uuid" => ColumnType.Uuid,
        "json" => ColumnType.Json,
        "array" => ColumnType.Array,
        _ => null,
    };
}

/// <summary>
/// One marker per source that contributed to a result.
/// </summary>
/// <remarks>
/// A cross-source result carries a <em>vector</em> of markers precisely so it is never described as one atomic
/// snapshot: no single engine's snapshot covers two engines, and pretending otherwise would be a provenance
/// claim nobody can honour.
/// </remarks>
public sealed record SnapshotMarker
{
    /// <summary>Gets the source that contributed.</summary>
    public required string SourceId { get; init; }

    /// <summary>Gets the engine-native marker: a snapshot, a table version, a manifest id.</summary>
    public string? Marker { get; init; }

    /// <summary>Gets the isolation level the read ran under.</summary>
    public string? Isolation { get; init; }

    /// <summary>Gets when the read started, as the engine reported it.</summary>
    public string? StartedAt { get; init; }

    /// <summary>Gets when the read ended, as the engine reported it.</summary>
    public string? EndedAt { get; init; }

    /// <summary>Gets the replay level, as an open vocabulary.</summary>
    public required string ReplayLevel { get; init; }

    /// <summary>Gets when the marker stops being replayable.</summary>
    public string? ReplayExpiresAt { get; init; }
}

/// <summary>
/// How current the data behind a result was.
/// </summary>
public sealed record Freshness
{
    /// <summary>Gets the source's watermark.</summary>
    public string? Watermark { get; init; }

    /// <summary>Gets when the data was observed.</summary>
    public string? ObservedAt { get; init; }

    /// <summary>Gets how far behind the source the result was, in seconds.</summary>
    public long? LagSeconds { get; init; }
}

/// <summary>
/// The run that produced a result.
/// </summary>
public sealed record Execution
{
    /// <summary>Gets when the run started.</summary>
    public required string StartedAt { get; init; }

    /// <summary>Gets when the run ended.</summary>
    public required string EndedAt { get; init; }

    /// <summary>
    /// Gets the identity the <em>source</em> saw, such as a row-level-security role.
    /// </summary>
    /// <remarks>
    /// Deliberately not the end user's identity: what matters for reproducing a result is the principal the
    /// engine applied its own policy to, not who asked.
    /// </remarks>
    public string? EffectivePrincipal { get; init; }

    /// <summary>Gets the engine-side correlation id for the statement.</summary>
    public string? StatementId { get; init; }
}
