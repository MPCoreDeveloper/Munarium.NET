namespace Munarium.Evidence;

using Munarium.Claims;
using Munarium.Retrieval;

/// <summary>
/// Why a layer produced nothing usable.
/// </summary>
/// <remarks>
/// Typed, so composition can disclose the reason without leaking what it could not show.
/// </remarks>
public sealed record EvidenceRefusal
{
    /// <summary>Gets the kebab-case code, matching the problem registry.</summary>
    public required string Code { get; init; }

    /// <summary>
    /// Gets the message, which is safe to show a caller.
    /// </summary>
    /// <remarks>
    /// It must never name a source the caller could not otherwise see - the hidden-required-layer rule: a caller
    /// who cannot see a source must not learn of it from the shape of a refusal.
    /// </remarks>
    public required string Message { get; init; }

    /// <summary>Gets the source the refusal is about, when naming it is safe.</summary>
    public string? Source { get; init; }
}

/// <summary>
/// A count with the coverage that makes it meaningful.
/// </summary>
/// <remarks>
/// A bare number is not evidence: "1,204" is only an answer if you also know what was counted and what was
/// excluded.
/// </remarks>
public sealed record CountBlock
{
    /// <summary>Gets the count.</summary>
    public required long Value { get; init; }

    /// <summary>Gets how many source rows the run covered.</summary>
    public long? RowsCovered { get; init; }

    /// <summary>Gets how many rows were excluded.</summary>
    public long? RowsExcluded { get; init; }

    /// <summary>Gets the reason rows were excluded.</summary>
    public string? ExclusionReason { get; init; }

    /// <summary>Gets the sealed artifact the count came from.</summary>
    public string? EvidenceId { get; init; }
}

/// <summary>
/// A typed result table.
/// </summary>
public sealed record TableBlock
{
    /// <summary>Gets the column names, in order.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>
    /// Gets the row cells as canonical text.
    /// </summary>
    /// <remarks>
    /// Text and never a JSON number, because a <c>decimal(38,2)</c> does not survive an IEEE-754 double: a value
    /// that is rounded on the way into the answer is a value the sealed bytes would disagree with.
    /// </remarks>
    public required IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; }

    /// <summary>
    /// Gets the id of each row, positionally aligned with <see cref="Rows"/>.
    /// </summary>
    /// <remarks>
    /// Taken from the sealer and never invented here. A keyed result's row id is its key (<c>EMEA</c>) rather than
    /// its position, and a citation has to resolve against the id the artifact can actually replay - numbering
    /// the rows here would reject every correct citation. Empty for a block with no sealed identity, in which case
    /// a renderer falls back to one-based positions.
    /// </remarks>
    public IReadOnlyList<string> RowIds { get; init; } = [];

    /// <summary>Gets a value indicating whether rows were cut off.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Gets the sealed artifact, so every cell is citable.</summary>
    public string? EvidenceId { get; init; }
}

/// <summary>
/// A pinned slice of the ledger's own facts.
/// </summary>
/// <param name="Claims">The claims the layer contributed.</param>
public sealed record LedgerFactSlice(IReadOnlyList<Claim> Claims);

/// <summary>
/// The document-retrieval path's contribution.
/// </summary>
/// <param name="Hits">The chunks retrieval returned.</param>
public sealed record DocumentHits(IReadOnlyList<RetrievedChunk> Hits);

/// <summary>
/// What a layer contributed.
/// </summary>
/// <remarks>
/// A closed union: a new case is a deliberate, compile-checked change to what an answer can be built from.
/// </remarks>
public readonly union EvidenceBlock(TableBlock, CountBlock, LedgerFactSlice, DocumentHits, EvidenceRefusal);

/// <summary>
/// The questions an answer may ask of a block, and the names the wire carries for them.
/// </summary>
public static class EvidenceBlockKinds
{
    /// <summary>May an answer make a completeness claim on this block?</summary>
    /// <remarks>
    /// A truncated table and a refusal are the obvious noes. Document hits are the one worth stating: retrieval
    /// returns the top-k it found, never a proof that nothing else exists, so treating a good search as exhaustive
    /// is how a system says "there are no other contracts" when it means "I found three".
    /// </remarks>
    /// <param name="block">The block.</param>
    /// <returns><see langword="true"/> when a completeness claim is permissible.</returns>
    public static bool SupportsCompleteness(this EvidenceBlock block) => block switch
    {
        TableBlock table => !table.Truncated,
        CountBlock => true,
        _ => false,
    };

    /// <summary>Did the layer produce anything an answer can use?</summary>
    /// <param name="block">The block.</param>
    /// <returns><see langword="true"/> when the block carries nothing usable.</returns>
    public static bool IsEmpty(this EvidenceBlock block) => block switch
    {
        DocumentHits hits => hits.Hits.Count == 0,
        TableBlock table => table.Rows.Count == 0,
        LedgerFactSlice slice => slice.Claims.Count == 0,
        EvidenceRefusal => true,
        _ => false,
    };

    /// <summary>Gets a value indicating whether the layer declined.</summary>
    /// <param name="block">The block.</param>
    /// <returns><see langword="true"/> when the block is a refusal.</returns>
    public static bool IsRefusal(this EvidenceBlock block) => block is EvidenceRefusal;

    /// <summary>Gets the sealed artifact behind a block, when there is one.</summary>
    /// <param name="block">The block.</param>
    /// <returns>The artifact's identity, or <see langword="null"/>.</returns>
    public static string? EvidenceId(this EvidenceBlock block) => block switch
    {
        TableBlock table => table.EvidenceId,
        CountBlock count => count.EvidenceId,
        _ => null,
    };

    /// <summary>Gets the block's kind, as a short stable label.</summary>
    /// <param name="block">The block.</param>
    /// <returns>The kind's name.</returns>
    public static string KindName(this EvidenceBlock block) => block switch
    {
        TableBlock => "complete_table",
        CountBlock => "count",
        LedgerFactSlice => "fact_slice",
        DocumentHits => "document_hits",
        _ => "refusal",
    };
}
