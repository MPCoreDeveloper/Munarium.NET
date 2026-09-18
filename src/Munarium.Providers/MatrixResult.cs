namespace Munarium.Providers;

using System.Globalization;
using System.Text.Json;
using Munarium.Evidence;
using Munarium.Ledger;

/// <summary>
/// Turns a data view's answer into this server's block.
/// </summary>
/// <remarks>
/// The contract's block is tagged by <c>kind</c> and carries the whole manifest beside its data. Two things are taken
/// verbatim rather than re-derived, and both matter.
/// <para>
/// Row ids come from the plane. <c>manifest.identity.row_id_rule</c> is <c>keys</c> for a keyed result, so a row's id is
/// its key (<c>EMEA</c>) and not its position. Numbering rows here would mean an answer citing the id the artifact can
/// actually replay while this server checked it against an invented <c>r0003</c> - and every correct citation would be
/// rejected. The one-based fallback is only for a row the plane sent no id for, and it matches how the context renders
/// them.
/// </para>
/// <para>
/// Cells stay text. A <c>decimal(38,2)</c> does not survive an IEEE-754 double, and exactness is the entire reason the
/// structured plane exists. A count's <c>value</c> is a string in the contract for the same reason, and is parsed to a
/// number only for the block's own field.
/// </para>
/// </remarks>
public static class MatrixResult
{
    /// <summary>
    /// Parses an answer the plane returned.
    /// </summary>
    /// <param name="body">The answer's body.</param>
    /// <param name="view">The view it is about, for a refusal that names it.</param>
    /// <returns>The block, which is a refusal when the answer was one.</returns>
    public static EvidenceBlock Parse(JsonElement body, string view)
    {
        var kind = PayloadJson.Text(body, "kind") ?? string.Empty;
        var evidenceId = PayloadJson.Text(body, "evidence_id");
        var manifest = PayloadJson.Member(body, "manifest");
        var completeness = manifest is { } declared ? PayloadJson.Member(declared, "completeness") : null;

        // The manifest is authoritative on truncation; the block's own flag is a convenience copy. If they ever
        // disagree, believe the sealed one: believing the copy would let a truncated read back a completeness claim.
        var truncated = (completeness is { } coverage ? BoolOf(coverage, "truncated") : null)
            ?? BoolOf(body, "truncated")
            ?? false;

        return kind switch
        {
            "count" => CountOf(body, completeness, evidenceId, view),
            "complete_table" => TableOf(body, manifest, truncated, evidenceId),
            "refusal" => RefusalOf(body, view),
            _ => EvidenceRefusals.Refuse(
                EvidenceRefusalCodes.SourceUnavailable,
                $"the structured-evidence plane returned an unrecognised block kind '{kind}'",
                view),
        };
    }

    /// <summary>
    /// Translates a refusal the plane returned, without reinterpreting it.
    /// </summary>
    /// <remarks>
    /// The plane's <c>class</c> is a closed set and its <c>code</c> is the specific reason, and the code is what an
    /// operator acts on - so the code is what survives. A refusal that arrives without one is reported as a rejected
    /// request rather than guessed at. Reporting a request defect as an outage would send an operator to check the
    /// plane's health when the fault is in their own profile.
    /// </remarks>
    /// <param name="body">The answer's body.</param>
    /// <param name="view">The view it is about.</param>
    /// <returns>The refusal block.</returns>
    public static EvidenceBlock RefusalOf(JsonElement body, string view)
    {
        var typed = PayloadJson.Member(body, "refusal") ?? body;
        var code = PayloadJson.Text(typed, "code") ?? EvidenceRefusalCodes.SourceRequestRejected;

        // The plane's own message is deliberately not forwarded: it is written for an operator of that plane and may
        // name a source, a class or a column this caller has no clearance for.
        return EvidenceRefusals.Refuse(code, "the structured-evidence plane declined this request", view);
    }

    /// <summary>Reads a count, with the coverage that makes it meaningful.</summary>
    private static EvidenceBlock CountOf(
        JsonElement body,
        JsonElement? completeness,
        string? evidenceId,
        string view)
    {
        // A string in the contract, deliberately - the same exactness that keeps 900000.50 distinct from 900000.5. An
        // unreadable one is a contract violation, and 0 would be a lie dressed as an answer.
        if (PayloadJson.Text(body, "value") is not { } text
            || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return EvidenceRefusals.Refuse(
                EvidenceRefusalCodes.SourceUnavailable,
                "the structured-evidence plane returned a count with no readable value",
                view);
        }

        long? covered = null;
        long? excluded = null;
        string? reason = null;

        if (completeness is { } coverage)
        {
            covered = PayloadJson.OptionalNumber(coverage, "rows_covered");
            excluded = PayloadJson.OptionalNumber(coverage, "rows_excluded");
            reason = PayloadJson.Text(coverage, "exclusion_reason");
        }

        return new CountBlock
        {
            Value = value,
            RowsCovered = covered,
            RowsExcluded = excluded,
            ExclusionReason = reason,
            EvidenceId = evidenceId,
        };
    }

    /// <summary>Reads a table, taking its columns and its row ids from the sealed manifest.</summary>
    private static EvidenceBlock TableOf(
        JsonElement body,
        JsonElement? manifest,
        bool truncated,
        string? evidenceId)
    {
        var columns = new List<string>();

        if (manifest is { } declared
            && PayloadJson.Member(declared, "schema") is { } schema
            && schema.TryGetProperty("columns", out var declaredColumns)
            && declaredColumns.ValueKind == JsonValueKind.Array)
        {
            foreach (var column in declaredColumns.EnumerateArray())
            {
                columns.Add(PayloadJson.Text(column, "name") ?? string.Empty);
            }
        }

        var rows = new List<IReadOnlyList<string?>>();
        var rowIds = new List<string>();

        if (body.TryGetProperty("rows", out var declaredRows) && declaredRows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in declaredRows.EnumerateArray())
            {
                rowIds.Add(PayloadJson.Text(row, "row_id")
                    ?? string.Create(CultureInfo.InvariantCulture, $"r{rows.Count + 1:0000}"));

                var cells = new List<string?>();

                if (row.TryGetProperty("cells", out var declaredCells)
                    && declaredCells.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cell in declaredCells.EnumerateArray())
                    {
                        cells.Add(CellText(cell));
                    }
                }

                rows.Add(cells);
            }
        }

        return new TableBlock
        {
            Columns = columns,
            Rows = rows,
            RowIds = rowIds,
            Truncated = truncated,
            EvidenceId = evidenceId,
        };
    }

    /// <summary>
    /// Reads a cell as canonical text.
    /// </summary>
    /// <remarks>
    /// NULL and the empty string are different facts and are kept apart: a sealed null and a sealed empty string are
    /// both things the plane can hold, and a reader that collapses them cannot say which one it has. Anything else is
    /// written as the JSON it arrived as, which is what keeps a decimal that arrived as text exactly that text.
    /// </remarks>
    private static string? CellText(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => cell.GetString(),
        _ => cell.GetRawText(),
    };

    private static bool? BoolOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
