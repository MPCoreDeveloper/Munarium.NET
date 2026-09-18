namespace Munarium.Evidence;

using System.Text;
using System.Text.Json;
using Munarium.Ledger;

/// <summary>
/// How an artifact's manifest is written down and read back.
/// </summary>
/// <remarks>
/// Hand-written rather than reflected, for the reason the index manifest's codec is: the shape is part of the record, so
/// it is stated here once instead of being derived from whatever the type happens to look like today - and the read
/// ignores what it does not know, so adding a field to a manifest does not make yesterday's row unreadable.
/// <para>
/// An absent value is written as JSON null rather than omitted. The manifest's own rule is that absent means "not
/// applicable to this kind" and never "unknown", so a codec that dropped the field would leave the next reader unable to
/// tell a null this codec wrote from a field a later producer added - the same distinction, one layer down.
/// </para>
/// <para>
/// The kind and the row-id rule are spelled explicitly rather than through their member names, for the reason
/// <see cref="ColumnTypeNames"/> gives: the stored record is a vocabulary, and a member renamed for a reader of C# must
/// not silently rename what is on disk.
/// </para>
/// </remarks>
public static class EvidenceManifestCodec
{
    /// <summary>Writes a manifest as JSON text.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(EvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var payload = PayloadJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("contract_version", manifest.ContractVersion);
            writer.WriteString("canon", manifest.Canon);
            PayloadJson.Optional(writer, "evidence_id", manifest.EvidenceId);
            writer.WriteString("tenant", manifest.Tenant);
            writer.WriteString("kind", manifest.Kind.ToWireName());
            writer.WriteString("logical_result_hash", manifest.LogicalResultHash);
            writer.WriteString("artifact_hash", manifest.ArtifactHash);
            writer.WriteNumber("bytes_length", manifest.BytesLength);
            writer.WriteString("media_type", manifest.MediaType);

            WriteSource(writer, manifest.Source);
            WriteVersions(writer, manifest.Versions);
            WritePlan(writer, manifest.Plan);
            WriteSchema(writer, manifest.Schema);
            WriteIdentity(writer, manifest.Identity);
            WriteCompleteness(writer, manifest.Completeness);
            WriteRedaction(writer, manifest.Redaction);
            WriteSnapshotVector(writer, manifest.SnapshotVector);
            WriteFreshness(writer, manifest.Freshness);
            WriteExecution(writer, manifest.Execution);
            WriteAuthorizationClass(writer, manifest.AuthorizationClass);
            WriteRetention(writer, manifest.Retention);

            writer.WriteEndObject();
        });

        return Encoding.UTF8.GetString(payload);
    }

    private static void WriteSource(Utf8JsonWriter writer, SourceRef source)
    {
        writer.WriteStartObject("source");
        writer.WriteString("source_id", source.SourceId);
        writer.WriteNumber("source_version", source.SourceVersion);
        writer.WriteString("adapter", source.Adapter);
        PayloadJson.Optional(writer, "adapter_version", source.AdapterVersion);
        PayloadJson.Optional(writer, "engine", source.Engine);
        PayloadJson.Optional(writer, "driver", source.Driver);
        writer.WriteEndObject();
    }

    private static void WriteVersions(Utf8JsonWriter writer, Versions versions)
    {
        writer.WriteStartObject("versions");
        PayloadJson.Optional(writer, "query_contract", versions.QueryContract);
        PayloadJson.Optional(writer, "claim_mapping", versions.ClaimMapping);
        PayloadJson.Optional(writer, "semantic_provider", versions.SemanticProvider);
        PayloadJson.Optional(writer, "render", versions.Render);
        PayloadJson.Optional(writer, "policy", versions.Policy);
        PayloadJson.Optional(writer, "compiler", versions.Compiler);
        writer.WriteEndObject();
    }

    private static void WritePlan(Utf8JsonWriter writer, PlanHashes? plan)
    {
        if (plan is null)
        {
            writer.WriteNull("plan");
            return;
        }

        writer.WriteStartObject("plan");
        PayloadJson.Optional(writer, "canonical_plan_hash", plan.CanonicalPlanHash);
        PayloadJson.Optional(writer, "bound_parameters_hash", plan.BoundParametersHash);
        writer.WriteEndObject();
    }

    private static void WriteSchema(Utf8JsonWriter writer, EvidenceSchema schema)
    {
        writer.WriteStartObject("schema");
        writer.WriteStartArray("columns");

        foreach (var column in schema.Columns)
        {
            writer.WriteStartObject();
            writer.WriteString("id", column.Id);
            writer.WriteString("name", column.Name);
            writer.WriteString("type", column.Type.ToContractName());
            writer.WriteBoolean("nullable", column.Nullable);
            Integer(writer, "scale", column.Scale);
            PayloadJson.Optional(writer, "unit", column.Unit);
            PayloadJson.Optional(writer, "additivity", column.Additivity);
            writer.WriteBoolean("key", column.Key);
            PayloadJson.Optional(writer, "element_type", column.ElementType);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, EvidenceIdentity identity)
    {
        writer.WriteStartObject("identity");
        writer.WriteString("row_id_rule", RuleName(identity.RowIdRule));
        Strings(writer, "order_by", identity.OrderBy);
        Number(writer, "rows", identity.Rows);
        writer.WriteEndObject();
    }

    private static void WriteCompleteness(Utf8JsonWriter writer, Completeness completeness)
    {
        writer.WriteStartObject("completeness");
        writer.WriteBoolean("truncated", completeness.Truncated);
        Number(writer, "declared_max_rows", completeness.DeclaredMaxRows);
        Number(writer, "rows_covered", completeness.RowsCovered);
        Number(writer, "rows_excluded", completeness.RowsExcluded);
        PayloadJson.Optional(writer, "exclusion_reason", completeness.ExclusionReason);
        writer.WriteEndObject();
    }

    private static void WriteRedaction(Utf8JsonWriter writer, Redaction? redaction)
    {
        if (redaction is null)
        {
            writer.WriteNull("redaction");
            return;
        }

        writer.WriteStartObject("redaction");
        Strings(writer, "denied_columns", redaction.DeniedColumns);
        writer.WriteBoolean("masked", redaction.Masked);
        writer.WriteEndObject();
    }

    private static void WriteSnapshotVector(Utf8JsonWriter writer, IReadOnlyList<SnapshotMarker> markers)
    {
        writer.WriteStartArray("snapshot_vector");

        foreach (var marker in markers)
        {
            writer.WriteStartObject();
            writer.WriteString("source_id", marker.SourceId);
            PayloadJson.Optional(writer, "marker", marker.Marker);
            PayloadJson.Optional(writer, "isolation", marker.Isolation);
            PayloadJson.Optional(writer, "started_at", marker.StartedAt);
            PayloadJson.Optional(writer, "ended_at", marker.EndedAt);
            writer.WriteString("replay_level", marker.ReplayLevel);
            PayloadJson.Optional(writer, "replay_expires_at", marker.ReplayExpiresAt);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteFreshness(Utf8JsonWriter writer, Freshness? freshness)
    {
        if (freshness is null)
        {
            writer.WriteNull("freshness");
            return;
        }

        writer.WriteStartObject("freshness");
        PayloadJson.Optional(writer, "watermark", freshness.Watermark);
        PayloadJson.Optional(writer, "observed_at", freshness.ObservedAt);
        Number(writer, "lag_seconds", freshness.LagSeconds);
        writer.WriteEndObject();
    }

    private static void WriteExecution(Utf8JsonWriter writer, Execution execution)
    {
        writer.WriteStartObject("execution");
        writer.WriteString("started_at", execution.StartedAt);
        writer.WriteString("ended_at", execution.EndedAt);
        PayloadJson.Optional(writer, "effective_principal", execution.EffectivePrincipal);
        PayloadJson.Optional(writer, "statement_id", execution.StatementId);
        writer.WriteEndObject();
    }

    private static void WriteAuthorizationClass(Utf8JsonWriter writer, AuthorizationClass authorizationClass)
    {
        writer.WriteStartObject("authorization_class");
        PayloadJson.Optional(writer, "name", authorizationClass.Name);
        writer.WriteNumber("access_level", authorizationClass.AccessLevel);
        Strings(writer, "compartments", authorizationClass.Compartments);
        writer.WriteEndObject();
    }

    private static void WriteRetention(Utf8JsonWriter writer, Retention? retention)
    {
        if (retention is null)
        {
            writer.WriteNull("retention");
            return;
        }

        writer.WriteStartObject("retention");
        PayloadJson.Optional(writer, "expires_at", retention.ExpiresAt);
        writer.WriteBoolean("legal_hold", retention.LegalHold);
        PayloadJson.Optional(writer, "purged_at", retention.PurgedAt);
        writer.WriteEndObject();
    }

    /// <summary>Reads a manifest back from JSON text.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="what">What is being read, for the failure message.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="FormatException">
    /// Thrown when the text is not JSON, or when a field the manifest cannot do without is missing.
    /// </exception>
    public static EvidenceManifest FromJson(string json, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = PayloadJson.Read(Encoding.UTF8.GetBytes(json), what);
        var root = document.RootElement;

        return new EvidenceManifest
        {
            ContractVersion = PayloadJson.RequiredText(root, "contract_version", what),
            Canon = PayloadJson.RequiredText(root, "canon", what),
            EvidenceId = PayloadJson.Text(root, "evidence_id"),
            Tenant = PayloadJson.RequiredText(root, "tenant", what),
            Kind = KindOf(PayloadJson.RequiredText(root, "kind", what), what),
            LogicalResultHash = PayloadJson.RequiredText(root, "logical_result_hash", what),
            ArtifactHash = PayloadJson.RequiredText(root, "artifact_hash", what),
            BytesLength = PayloadJson.RequiredNumber(root, "bytes_length", what),
            MediaType = PayloadJson.RequiredText(root, "media_type", what),
            Source = Source(PayloadJson.RequiredMember(root, "source", what), what),
            Versions = Versions(PayloadJson.RequiredMember(root, "versions", what)),
            Plan = Plan(PayloadJson.Member(root, "plan")),
            Schema = Schema(PayloadJson.RequiredMember(root, "schema", what), what),
            Identity = Identity(PayloadJson.RequiredMember(root, "identity", what), what),
            Completeness = Completeness(PayloadJson.RequiredMember(root, "completeness", what)),
            Redaction = Redaction(PayloadJson.Member(root, "redaction")),
            SnapshotVector = SnapshotVector(root, what),
            Freshness = Freshness(PayloadJson.Member(root, "freshness")),
            Execution = Execution(PayloadJson.RequiredMember(root, "execution", what), what),
            AuthorizationClass = AuthorizationClass(
                PayloadJson.RequiredMember(root, "authorization_class", what), what),
            Retention = Retention(PayloadJson.Member(root, "retention")),
        };
    }

    private static SourceRef Source(JsonElement element, string what) => new(
        PayloadJson.RequiredText(element, "source_id", what),
        PayloadJson.RequiredNumber(element, "source_version", what),
        PayloadJson.RequiredText(element, "adapter", what))
    {
        AdapterVersion = PayloadJson.Text(element, "adapter_version"),
        Engine = PayloadJson.Text(element, "engine"),
        Driver = PayloadJson.Text(element, "driver"),
    };

    private static Versions Versions(JsonElement element) => new()
    {
        QueryContract = PayloadJson.Text(element, "query_contract"),
        ClaimMapping = PayloadJson.Text(element, "claim_mapping"),
        SemanticProvider = PayloadJson.Text(element, "semantic_provider"),
        Render = PayloadJson.Text(element, "render"),
        Policy = PayloadJson.Text(element, "policy"),
        Compiler = PayloadJson.Text(element, "compiler"),
    };

    private static PlanHashes? Plan(JsonElement? element) => element is not { } plan
        ? null
        : new PlanHashes
        {
            CanonicalPlanHash = PayloadJson.Text(plan, "canonical_plan_hash"),
            BoundParametersHash = PayloadJson.Text(plan, "bound_parameters_hash"),
        };

    private static EvidenceSchema Schema(JsonElement element, string what)
    {
        if (!element.TryGetProperty("columns", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return new EvidenceSchema([]);
        }

        var columns = new List<EvidenceColumn>(array.GetArrayLength());

        foreach (var column in array.EnumerateArray())
        {
            columns.Add(Column(column, what));
        }

        return new EvidenceSchema(columns);
    }

    private static EvidenceColumn Column(JsonElement element, string what)
    {
        var declared = PayloadJson.RequiredText(element, "type", what);

        return new EvidenceColumn
        {
            Id = PayloadJson.RequiredText(element, "id", what),
            Name = PayloadJson.RequiredText(element, "name", what),
            Type = ColumnTypeNames.ParseType(declared)
                ?? throw new FormatException($"{what} names a column type this server does not know: '{declared}'."),
            Nullable = PayloadJson.Flag(element, "nullable"),
            Scale = (int?)PayloadJson.OptionalNumber(element, "scale"),
            Unit = PayloadJson.Text(element, "unit"),
            Additivity = PayloadJson.Text(element, "additivity"),
            Key = PayloadJson.Flag(element, "key"),
            ElementType = PayloadJson.Text(element, "element_type"),
        };
    }

    private static EvidenceIdentity Identity(JsonElement element, string what) =>
        new(Rule(PayloadJson.RequiredText(element, "row_id_rule", what), what))
        {
            OrderBy = PayloadJson.Strings(element, "order_by"),
            Rows = PayloadJson.OptionalNumber(element, "rows"),
        };

    private static Completeness Completeness(JsonElement element) => new()
    {
        Truncated = PayloadJson.Flag(element, "truncated"),
        DeclaredMaxRows = PayloadJson.OptionalNumber(element, "declared_max_rows"),
        RowsCovered = PayloadJson.OptionalNumber(element, "rows_covered"),
        RowsExcluded = PayloadJson.OptionalNumber(element, "rows_excluded"),
        ExclusionReason = PayloadJson.Text(element, "exclusion_reason"),
    };

    private static Redaction? Redaction(JsonElement? element) => element is not { } redaction
        ? null
        : new Redaction
        {
            DeniedColumns = PayloadJson.Strings(redaction, "denied_columns"),
            Masked = PayloadJson.Flag(redaction, "masked"),
        };

    private static List<SnapshotMarker> SnapshotVector(JsonElement root, string what)
    {
        if (!root.TryGetProperty("snapshot_vector", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var markers = new List<SnapshotMarker>(array.GetArrayLength());

        foreach (var marker in array.EnumerateArray())
        {
            markers.Add(Marker(marker, what));
        }

        return markers;
    }

    private static SnapshotMarker Marker(JsonElement element, string what) => new()
    {
        SourceId = PayloadJson.RequiredText(element, "source_id", what),
        Marker = PayloadJson.Text(element, "marker"),
        Isolation = PayloadJson.Text(element, "isolation"),
        StartedAt = PayloadJson.Text(element, "started_at"),
        EndedAt = PayloadJson.Text(element, "ended_at"),
        ReplayLevel = PayloadJson.RequiredText(element, "replay_level", what),
        ReplayExpiresAt = PayloadJson.Text(element, "replay_expires_at"),
    };

    private static Freshness? Freshness(JsonElement? element) => element is not { } freshness
        ? null
        : new Freshness
        {
            Watermark = PayloadJson.Text(freshness, "watermark"),
            ObservedAt = PayloadJson.Text(freshness, "observed_at"),
            LagSeconds = PayloadJson.OptionalNumber(freshness, "lag_seconds"),
        };

    private static Execution Execution(JsonElement element, string what) => new()
    {
        StartedAt = PayloadJson.RequiredText(element, "started_at", what),
        EndedAt = PayloadJson.RequiredText(element, "ended_at", what),
        EffectivePrincipal = PayloadJson.Text(element, "effective_principal"),
        StatementId = PayloadJson.Text(element, "statement_id"),
    };

    private static AuthorizationClass AuthorizationClass(JsonElement element, string what) => new()
    {
        Name = PayloadJson.Text(element, "name"),
        AccessLevel = (int)PayloadJson.RequiredNumber(element, "access_level", what),
        Compartments = PayloadJson.Strings(element, "compartments"),
    };

    private static Retention? Retention(JsonElement? element) => element is not { } retention
        ? null
        : new Retention
        {
            ExpiresAt = PayloadJson.Text(retention, "expires_at"),
            LegalHold = PayloadJson.Flag(retention, "legal_hold"),
            PurgedAt = PayloadJson.Text(retention, "purged_at"),
        };

    private static EvidenceKind KindOf(string name, string what) =>
        EvidenceKindNames.ParseKind(name)
            ?? throw new FormatException($"{what} names an evidence kind this server does not know: '{name}'.");

    private static string RuleName(RowIdRule rule) => rule == RowIdRule.Position ? "position" : "keys";

    private static RowIdRule Rule(string name, string what) => name switch
    {
        "keys" => RowIdRule.Keys,
        "position" => RowIdRule.Position,
        _ => throw new FormatException($"{what} names a row-id rule this server does not know: '{name}'."),
    };

    private static void Number(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } present)
        {
            writer.WriteNumber(name, present);
            return;
        }

        writer.WriteNull(name);
    }

    private static void Integer(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } present)
        {
            writer.WriteNumber(name, present);
            return;
        }

        writer.WriteNull(name);
    }

    private static void Strings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);

        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
