namespace Munarium.Evidence;

using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// Composes the hierarchy's blocks into the context a model would be given.
/// </summary>
/// <param name="Context">The context text.</param>
/// <param name="LayersUsed">How many layers made it in, in whole or in part.</param>
/// <param name="LayersDropped">The layers that did not fit, in execution order.</param>
public sealed record ComposedContext(string Context, int LayersUsed, IReadOnlyList<string> LayersDropped);

/// <summary>
/// One row an answer may cite, as the renderer numbered it.
/// </summary>
/// <param name="RowId">The row's id, which is the sealer's when it had one.</param>
/// <param name="Cells">The row's cells as canonical text, with a null cell spelled out.</param>
public sealed record ServedRow(string RowId, IReadOnlyList<string> Cells);

/// <summary>
/// A sealed table this hierarchy served, so a citation can be resolved against what the model read.
/// </summary>
/// <param name="EvidenceId">The sealed artifact.</param>
/// <param name="Rows">The rows, each with the id the context showed.</param>
public sealed record ServedEvidence(string EvidenceId, IReadOnlyList<ServedRow> Rows);

/// <summary>
/// Turns blocks into model context, highest trust first, under a character budget.
/// </summary>
/// <remarks>
/// Composition order is trust order: the highest-trust evidence occupies the budget first. A layer marked
/// <see cref="EvidenceLayer.PreserveCompleteResult"/> is taken whole or not at all, because half a table is not a
/// smaller true answer but a false one - a model shown nine of twelve rows will answer about twelve.
/// <para>
/// A refusal is <em>disclosed</em> to the model rather than hidden from it: an answer built without the register
/// should be able to say the register was not consulted, and it can only do that if it was told. What the refusal
/// does not disclose is its source, which stays in the decision and the audit trail - the model is told that a
/// layer declined and why, in words that name nothing the caller could not otherwise see.
/// </para>
/// </remarks>
public static class HierarchyComposer
{
    /// <summary>How a cell with no value is rendered: the word, never an empty cell.</summary>
    /// <remarks>
    /// "No value recorded" and "the empty string" are different facts, and rendering both as nothing would merge
    /// them in the one place a reader cannot tell them apart.
    /// </remarks>
    public const string NullCell = "NULL";

    /// <summary>
    /// Composes the blocks into context.
    /// </summary>
    /// <param name="plan">The plan the blocks came from, which is where per-layer budgets live.</param>
    /// <param name="blocks">The blocks, in execution order.</param>
    /// <param name="budget">The total character budget.</param>
    /// <returns>The context, and what happened to each layer.</returns>
    public static ComposedContext Compose(EvidencePlan plan, IReadOnlyList<LayerBlock> blocks, int budget)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentOutOfRangeException.ThrowIfNegative(budget);

        var context = new StringBuilder();
        var used = 0;
        var dropped = new List<string>();

        foreach (var (name, block) in blocks)
        {
            var layer = plan.Layers.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            var rendered = Render(name, block);

            if (rendered.Length == 0)
            {
                continue;
            }

            // The room for THIS layer is the smaller of what the profile has left and the layer's own cap. A
            // per-layer budget that only the grammar validated and nobody read would let a supporting layer
            // declared at four thousand characters consume the whole profile budget.
            var room = Math.Min(Math.Max(0, budget - context.Length), layer?.ContextCharBudget ?? int.MaxValue);

            if (rendered.Length <= room)
            {
                context.Append(rendered);
                used++;
                continue;
            }

            // Whole or nothing: a preserved layer is never partially served.
            if (layer?.PreserveCompleteResult == true || room <= 0)
            {
                dropped.Add(name);
                continue;
            }

            var cut = Math.Min(room, rendered.Length);

            // The hazard differs by platform and is worth naming: Rust has to find a char boundary because slicing
            // a UTF-8 string mid-character panics. .NET slices UTF-16, where the equivalent hazard is splitting a
            // surrogate pair - so cutting before a high surrogate keeps the text from ending in half a character.
            if (cut < rendered.Length && char.IsHighSurrogate(rendered[cut - 1]))
            {
                cut--;
            }

            if (cut <= 0)
            {
                dropped.Add(name);
                continue;
            }

            context.Append(rendered, 0, cut);
            used++;
        }

        return new ComposedContext(context.ToString(), used, dropped);
    }

    /// <summary>
    /// Gets the id of a row: the sealer's, falling back to a one-based position when the block carries none.
    /// </summary>
    /// <remarks>
    /// One function, called by both the renderer and <see cref="ServedEvidence"/>, because the model cites the id it
    /// was <em>shown</em> and a checker resolves the id it was <em>given</em>. Two implementations merely supposed
    /// to agree is how a citation check starts rejecting correct citations - which is the worst kind of check,
    /// because it punishes the very behaviour it exists to encourage.
    /// </remarks>
    /// <param name="table">The table.</param>
    /// <param name="index">The row's position.</param>
    /// <returns>The row's id.</returns>
    public static string TableRowId(TableBlock table, int index)
    {
        ArgumentNullException.ThrowIfNull(table);

        return index >= 0 && index < table.RowIds.Count
            ? table.RowIds[index]
            : string.Create(CultureInfo.InvariantCulture, $"r{index + 1:0000}");
    }

    /// <summary>
    /// Lists the sealed rows this hierarchy served, for the evidence checks.
    /// </summary>
    /// <param name="blocks">The blocks the run produced.</param>
    /// <returns>One entry per sealed table, with the row ids the context showed.</returns>
    public static IReadOnlyList<ServedEvidence> ServedEvidence(IReadOnlyList<LayerBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var served = new List<ServedEvidence>();

        foreach (var entry in blocks)
        {
            if (entry.Block is TableBlock table && table.EvidenceId is { Length: > 0 } id)
            {
                served.Add(new ServedEvidence(
                    id,
                    [
                        .. table.Rows.Select((row, index) => new ServedRow(
                            TableRowId(table, index),
                            [.. row.Select(cell => cell ?? NullCell)])),
                    ]));
            }
        }

        return served;
    }

    /// <summary>Renders one block as context text.</summary>
    /// <remarks>
    /// Every block is labelled with its layer and, where it matters, whether it is complete: a model that cannot
    /// tell a complete table from a truncated one will treat both as complete.
    /// </remarks>
    /// <param name="layer">The layer's name.</param>
    /// <param name="block">The block.</param>
    /// <returns>The text, or an empty string when the block carries nothing.</returns>
    private static string Render(string layer, EvidenceBlock block) => block switch
    {
        DocumentHits hits => RenderHits(layer, hits),
        TableBlock table => RenderTable(layer, table),
        CountBlock count => RenderCount(layer, count),
        LedgerFactSlice slice => RenderFacts(layer, slice),
        EvidenceRefusal refusal => RenderRefusal(layer, refusal),

        // The union is closed, so this cannot happen. A case added later fails loudly here rather than quietly
        // contributing nothing to the context.
        _ => throw new UnreachableException("the evidence block union is closed"),
    };

    private static string RenderHits(string layer, DocumentHits hits)
    {
        var text = new StringBuilder();

        foreach (var hit in hits.Hits)
        {
            text.Append('[').Append(layer).Append('/').Append(hit.Source.ChunkId).Append("] ")
                .Append(hit.Text).Append("\n\n");
        }

        return text.ToString();
    }

    private static string RenderTable(string layer, TableBlock table)
    {
        var text = new StringBuilder()
            .Append('[').Append(layer).Append("] ")
            .Append(table.Truncated ? "TRUNCATED" : "COMPLETE")
            .Append(" result (").Append(Invariant(table.Rows.Count)).Append(" rows), columns: ")
            .AppendJoin(" | ", table.Columns)
            .Append('\n');

        if (table.EvidenceId is { Length: > 0 } evidenceId)
        {
            text.Append("evidence: ").Append(evidenceId).Append('\n');
        }

        for (var index = 0; index < table.Rows.Count; index++)
        {
            text.Append(TableRowId(table, index)).Append(" | ")
                .AppendJoin(" | ", table.Rows[index].Select(cell => cell ?? NullCell))
                .Append('\n');
        }

        return text.Append('\n').ToString();
    }

    private static string RenderCount(string layer, CountBlock count)
    {
        var text = new StringBuilder()
            .Append('[').Append(layer).Append("] count: ").Append(Invariant(count.Value)).Append('\n');

        if (count.RowsCovered is { } covered)
        {
            text.Append("rows covered: ").Append(Invariant(covered)).Append('\n');
        }

        if (count.RowsExcluded is { } excluded)
        {
            text.Append("rows excluded: ").Append(Invariant(excluded));

            if (count.ExclusionReason is { Length: > 0 } reason)
            {
                text.Append(" (").Append(reason).Append(')');
            }

            text.Append('\n');
        }

        if (count.EvidenceId is { Length: > 0 } evidenceId)
        {
            text.Append("evidence: ").Append(evidenceId).Append('\n');
        }

        return text.Append('\n').ToString();
    }

    private static string RenderFacts(string layer, LedgerFactSlice slice)
    {
        var text = new StringBuilder().Append('[').Append(layer).Append("] recorded facts\n");

        foreach (var claim in slice.Claims)
        {
            text.Append(claim.ClaimKey).Append(" = ").Append(claim.Value).Append('\n');
        }

        return text.Append('\n').ToString();
    }

    // The refusal's source is deliberately not rendered: the model is told that a layer declined and why, in words
    // that name nothing the caller could not otherwise see. The source stays in the decision and the audit trail.
    private static string RenderRefusal(string layer, EvidenceRefusal refusal) =>
        $"[{layer}] no evidence available ({refusal.Code}): {refusal.Message}\n\n";

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
