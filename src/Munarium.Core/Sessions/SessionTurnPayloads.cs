namespace Munarium.Sessions;

using System.Text;
using System.Text.Json;
using Munarium.Evidence;
using Munarium.Retrieval;

/// <summary>
/// The JSON a recorded turn carries.
/// </summary>
/// <remarks>
/// Written with a fixed-order writer rather than a serializer, like the kernel's other payloads: no reflection, no
/// options object that can drift between runtime versions, and nothing that reorders a field and quietly changes what a
/// reader sees. The store does not interpret any of it - a store that parsed a hit would be a second place a hit is
/// defined.
/// </remarks>
public static class SessionTurnPayloads
{
    /// <summary>Writes the merged hits.</summary>
    /// <param name="hits">The turn's merged hits.</param>
    /// <returns>The hits, as JSON.</returns>
    public static string Hits(IReadOnlyList<RetrievedChunk> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        return Json(writer =>
        {
            writer.WriteStartArray();

            foreach (var chunk in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("chunk_id", chunk.Source.ChunkId);
                writer.WriteString("source_path", chunk.Source.SourcePath);
                writer.WriteNumber("score", chunk.Score);
                writer.WriteString("text", chunk.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <summary>Writes the provenance the hits came with, one entry per collection that answered.</summary>
    /// <param name="envelopes">The envelopes, in the order the collections were searched.</param>
    /// <returns>The envelopes, as JSON.</returns>
    public static string Envelopes(IReadOnlyList<ProvenanceEnvelope> envelopes)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        return Json(writer =>
        {
            writer.WriteStartArray();

            foreach (var envelope in envelopes)
            {
                writer.WriteStartObject();
                writer.WriteString("index_version", envelope.IndexVersion);
                writer.WriteNumber("ledger_watermark", envelope.LedgerWatermark.Value);
                writer.WriteStartArray("sources");

                foreach (var source in envelope.Sources)
                {
                    writer.WriteStartObject();
                    writer.WriteString("chunk_id", source.ChunkId);
                    writer.WriteString("source_path", source.SourcePath);
                    writer.WriteString("content_hash", source.ContentHash);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <summary>Writes the completion audit: what the answer cost, and what was checked.</summary>
    /// <param name="outcome">The turn's completion.</param>
    /// <returns>The audit, as JSON.</returns>
    public static string Completion(TurnOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("text", outcome.Answer);
            writer.WriteNumber("input_tokens", outcome.InputTokens);
            writer.WriteNumber("output_tokens", outcome.OutputTokens);
            writer.WriteNumber("completions", outcome.Completions);
            writer.WriteBoolean("retried_for_truncation", outcome.RetriedForTruncation);
            writer.WriteStartObject("verification");
            writer.WriteStartArray("checks");

            foreach (var check in outcome.Checks)
            {
                writer.WriteStringValue(check);
            }

            writer.WriteEndArray();
            writer.WriteNumber("retries", outcome.Retries);
            WriteStrings(writer, "first_pass_violations", outcome.FirstPassViolations);
            WriteStrings(writer, "violations", outcome.Violations);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Writes the evidence-hierarchy decision: why the model saw what it saw.
    /// </summary>
    /// <remarks>
    /// About the decision rather than the content - which profile, which layers ran, which refused, whether a
    /// completeness claim was permissible - because the evidence itself is recorded beside it, and a decision that also
    /// carried rows would be a second copy of the evidence to keep in step.
    /// </remarks>
    /// <param name="decision">The decision.</param>
    /// <returns>The decision, as JSON.</returns>
    public static string Hierarchy(EvidenceHierarchyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("profile", decision.Profile);
            writer.WriteString("intent_kind", decision.IntentKind);
            writer.WriteBoolean("intent_explicit", decision.IntentExplicit);
            writer.WriteBoolean("completeness_available", decision.CompletenessAvailable);
            writer.WriteNumber("disclosed_conflicts", decision.DisclosedConflicts);
            writer.WriteString("conflicts_policy", decision.ConflictsPolicy);
            writer.WriteStartArray("layers");

            foreach (var layer in decision.Layers)
            {
                writer.WriteStartObject();

                // The enums' own names are the wire names: `required`, `optional`, `fallback`, and the three roles.
                writer.WriteString("layer", layer.Layer);
                writer.WriteString("role", layer.Role.ToString().ToLowerInvariant());
                writer.WriteString("requirement", layer.Requirement.ToString().ToLowerInvariant());
                writer.WriteString("block", layer.Block);
                writer.WriteString("evidence_id", layer.EvidenceId);
                writer.WriteBoolean("supports_completeness", layer.SupportsCompleteness);
                writer.WriteString("refusal_code", layer.RefusalCode);
                writer.WriteNumber("elapsed_ms", layer.ElapsedMilliseconds);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);

        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
