namespace Munarium.Server;

using Grpc.Core;
using Munarium.Wire;
using Munarium.Wire.Generated;

// The kernel's evidence types and the generated evidence messages share several names - a manifest, a schema, a column -
// so the kernel's namespace is aliased here and the generated names stay unqualified. The generated side is where the
// messages are referenced, which is where the shorter name earns its keep.
using Evidence = Munarium.Evidence;

/// <summary>
/// The evidence plane's messages, mapped to and from the kernel's model.
/// </summary>
/// <remarks>
/// Hand-written and field by field, for the reason the index manifest's mapping is: SharpPortico maps the OpenAPI
/// document onto messages and knows nothing about the kernel's types, so the correspondence between the two lives here,
/// where it can be read and checked against the contract.
/// <para>
/// Three things this mapping has to be careful about, each noted where it bites: the contract's spellings are recovered
/// from the generated enum's members rather than cast from its numbering, the parts the kernel requires are refused when
/// a caller omits them, and the numbers the contract leaves optional have no "absent" in proto3 - so zero reads as
/// absent and back.
/// </para>
/// </remarks>
internal static class EvidenceGrpcMapping
{
    /// <summary>Maps a manifest offered for sealing onto the kernel's model.</summary>
    internal static Evidence.EvidenceManifest ToEvidence(EvidenceManifest manifest)
    {
        var source = Required(manifest.Source, "source");
        var versions = Required(manifest.Versions, "versions");
        var schema = Required(manifest.Schema, "schema");
        var identity = Required(manifest.Identity, "identity");
        var completeness = Required(manifest.Completeness, "completeness");
        var execution = Required(manifest.Execution, "execution");
        var authorization = Required(manifest.AuthorizationClass, "authorization_class");

        return new()
        {
            ContractVersion = manifest.ContractVersion,
            Canon = CanonOf(manifest.Canon),
            EvidenceId = Empty(manifest.EvidenceId),
            Tenant = manifest.Tenant,
            Kind = KindOf(manifest.Kind),
            LogicalResultHash = manifest.LogicalResultHash,
            ArtifactHash = manifest.ArtifactHash,
            BytesLength = manifest.BytesLen,
            MediaType = MediaTypeOf(manifest.MediaType),
            Source = ToEvidence(source),
            Versions = ToEvidence(versions),
            Plan = manifest.Plan is { } plan ? ToEvidence(plan) : null,
            Schema = ToEvidence(schema),
            Identity = ToEvidence(identity),
            Completeness = ToEvidence(completeness),
            Redaction = manifest.Redaction is { } redaction ? ToEvidence(redaction) : null,
            SnapshotVector = [.. manifest.SnapshotVector.Select(ToEvidence)],
            Freshness = manifest.Freshness is { } freshness ? ToEvidence(freshness) : null,
            Execution = ToEvidence(execution),
            AuthorizationClass = ToEvidence(authorization),
            Retention = manifest.Retention is { } retention ? ToEvidence(retention) : null,
        };
    }

    private static Evidence.SourceRef ToEvidence(EvidenceSource source) => new(
        source.SourceId,
        source.SourceVersion,
        source.Adapter)
    {
        AdapterVersion = Empty(source.AdapterVersion),
        Engine = Empty(source.Engine),
        Driver = Empty(source.Driver),
    };

    private static Evidence.Versions ToEvidence(EvidenceVersions versions) => new()
    {
        QueryContract = Empty(versions.QueryContract),
        ClaimMapping = Empty(versions.ClaimMapping),
        SemanticProvider = Empty(versions.SemanticProvider),
        Render = Empty(versions.Render),
        Policy = Empty(versions.Policy),
        Compiler = Empty(versions.Compiler),
    };

    private static Evidence.PlanHashes ToEvidence(EvidencePlan plan) => new()
    {
        CanonicalPlanHash = Empty(plan.CanonicalPlanHash),
        BoundParametersHash = Empty(plan.BoundParametersHash),
    };

    private static Evidence.EvidenceSchema ToEvidence(EvidenceSchema schema) =>
        new([.. schema.Columns.Select(ToEvidence)]);

    private static Evidence.EvidenceColumn ToEvidence(EvidenceColumn column) => new()
    {
        Id = column.Id,
        Name = column.Name,
        Type = ColumnTypeOf(column.Type),
        Nullable = column.Nullable,
        Scale = column.Scale == 0 ? null : checked((int)column.Scale),
        Unit = Empty(column.Unit),
        Additivity = Empty(column.Additivity),
        Key = column.Key,
        ElementType = Empty(column.ElementType),
    };

    private static Evidence.EvidenceIdentity ToEvidence(EvidenceIdentity identity) =>
        new(RowIdRuleOf(identity.RowIdRule))
        {
            OrderBy = [.. identity.OrderBy],
            Rows = ZeroAsNone(identity.Rows),
        };

    private static Evidence.Completeness ToEvidence(EvidenceCompleteness completeness) => new()
    {
        Truncated = completeness.Truncated,
        DeclaredMaxRows = ZeroAsNone(completeness.DeclaredMaxRows),
        RowsCovered = ZeroAsNone(completeness.RowsCovered),
        RowsExcluded = ZeroAsNone(completeness.RowsExcluded),
        ExclusionReason = Empty(completeness.ExclusionReason),
    };

    private static Evidence.Redaction ToEvidence(EvidenceRedaction redaction) => new()
    {
        DeniedColumns = [.. redaction.DeniedColumns],
        Masked = redaction.Masked,
    };

    private static Evidence.SnapshotMarker ToEvidence(EvidenceSnapshotMarker marker) => new()
    {
        SourceId = marker.SourceId,
        Marker = Empty(marker.Marker),
        Isolation = Empty(marker.Isolation),
        StartedAt = Empty(marker.StartedAt),
        EndedAt = Empty(marker.EndedAt),
        ReplayLevel = marker.ReplayLevel,
        ReplayExpiresAt = Empty(marker.ReplayExpiresAt),
    };

    private static Evidence.Freshness ToEvidence(EvidenceFreshness freshness) => new()
    {
        Watermark = Empty(freshness.Watermark),
        ObservedAt = Empty(freshness.ObservedAt),
        LagSeconds = ZeroAsNone(freshness.LagSeconds),
    };

    private static Evidence.Execution ToEvidence(EvidenceExecution execution) => new()
    {
        StartedAt = execution.StartedAt,
        EndedAt = execution.EndedAt,
        EffectivePrincipal = Empty(execution.EffectivePrincipal),
        StatementId = Empty(execution.StatementId),
    };

    private static Evidence.AuthorizationClass ToEvidence(EvidenceAuthorizationClass authorization) => new()
    {
        Name = Empty(authorization.Name),
        AccessLevel = checked((int)authorization.AccessLevel),
        Compartments = [.. authorization.Compartments],
    };

    private static Evidence.Retention ToEvidence(EvidenceRetention retention) => new()
    {
        ExpiresAt = Empty(retention.ExpiresAt),
        LegalHold = retention.LegalHold,
        PurgedAt = Empty(retention.PurgedAt),
    };

    /// <summary>Maps the kernel's manifest onto the message a client reads.</summary>
    internal static EvidenceManifest ToMessage(Evidence.EvidenceManifest manifest)
    {
        var message = new EvidenceManifest
        {
            ContractVersion = manifest.ContractVersion,
            Canon = WireCanon(manifest.Canon),
            EvidenceId = manifest.EvidenceId ?? string.Empty,
            Tenant = manifest.Tenant,
            Kind = WireKindOf(manifest.Kind),
            LogicalResultHash = manifest.LogicalResultHash,
            ArtifactHash = manifest.ArtifactHash,
            BytesLen = manifest.BytesLength,
            MediaType = WireMediaType(manifest.MediaType),
            Source = ToMessage(manifest.Source),
            Versions = ToMessage(manifest.Versions),
            Schema = ToMessage(manifest.Schema),
            Identity = ToMessage(manifest.Identity),
            Completeness = ToMessage(manifest.Completeness),
            Execution = ToMessage(manifest.Execution),
            AuthorizationClass = ToMessage(manifest.AuthorizationClass),
        };

        message.Plan = manifest.Plan is { } plan ? ToMessage(plan) : null!;
        message.Redaction = manifest.Redaction is { } redaction ? ToMessage(redaction) : null!;
        message.Freshness = manifest.Freshness is { } freshness ? ToMessage(freshness) : null!;
        message.Retention = manifest.Retention is { } retention ? ToMessage(retention) : null!;
        message.SnapshotVector.AddRange(manifest.SnapshotVector.Select(ToMessage));

        return message;
    }

    private static EvidenceSource ToMessage(Evidence.SourceRef source) => new()
    {
        SourceId = source.SourceId,
        SourceVersion = source.SourceVersion,
        Adapter = source.Adapter,
        AdapterVersion = source.AdapterVersion ?? string.Empty,
        Engine = source.Engine ?? string.Empty,
        Driver = source.Driver ?? string.Empty,
    };

    private static EvidenceVersions ToMessage(Evidence.Versions versions) => new()
    {
        QueryContract = versions.QueryContract ?? string.Empty,
        ClaimMapping = versions.ClaimMapping ?? string.Empty,
        SemanticProvider = versions.SemanticProvider ?? string.Empty,
        Render = versions.Render ?? string.Empty,
        Policy = versions.Policy ?? string.Empty,
        Compiler = versions.Compiler ?? string.Empty,
    };

    private static EvidencePlan ToMessage(Evidence.PlanHashes plan) => new()
    {
        CanonicalPlanHash = plan.CanonicalPlanHash ?? string.Empty,
        BoundParametersHash = plan.BoundParametersHash ?? string.Empty,
    };

    private static EvidenceSchema ToMessage(Evidence.EvidenceSchema schema)
    {
        var message = new EvidenceSchema();
        message.Columns.AddRange(schema.Columns.Select(ToMessage));

        return message;
    }

    private static EvidenceColumn ToMessage(Evidence.EvidenceColumn column) => new()
    {
        Id = column.Id,
        Name = column.Name,
        Type = WireColumnType(column.Type),
        Nullable = column.Nullable,
        Scale = column.Scale ?? 0,
        Unit = column.Unit ?? string.Empty,
        Additivity = column.Additivity ?? string.Empty,
        Key = column.Key,
        ElementType = column.ElementType ?? string.Empty,
    };

    private static EvidenceIdentity ToMessage(Evidence.EvidenceIdentity identity) => new()
    {
        RowIdRule = WireRowIdRule(identity.RowIdRule),
        Rows = identity.Rows ?? 0,
        OrderBy = { identity.OrderBy },
    };

    private static EvidenceCompleteness ToMessage(Evidence.Completeness completeness) => new()
    {
        Truncated = completeness.Truncated,
        DeclaredMaxRows = completeness.DeclaredMaxRows ?? 0,
        RowsCovered = completeness.RowsCovered ?? 0,
        RowsExcluded = completeness.RowsExcluded ?? 0,
        ExclusionReason = completeness.ExclusionReason ?? string.Empty,
    };

    private static EvidenceRedaction ToMessage(Evidence.Redaction redaction) => new()
    {
        Masked = redaction.Masked,
        DeniedColumns = { redaction.DeniedColumns },
    };

    private static EvidenceSnapshotMarker ToMessage(Evidence.SnapshotMarker marker) => new()
    {
        SourceId = marker.SourceId,
        Marker = marker.Marker ?? string.Empty,
        Isolation = marker.Isolation ?? string.Empty,
        StartedAt = marker.StartedAt ?? string.Empty,
        EndedAt = marker.EndedAt ?? string.Empty,
        ReplayLevel = marker.ReplayLevel,
        ReplayExpiresAt = marker.ReplayExpiresAt ?? string.Empty,
    };

    private static EvidenceFreshness ToMessage(Evidence.Freshness freshness) => new()
    {
        Watermark = freshness.Watermark ?? string.Empty,
        ObservedAt = freshness.ObservedAt ?? string.Empty,
        LagSeconds = freshness.LagSeconds ?? 0,
    };

    private static EvidenceExecution ToMessage(Evidence.Execution execution) => new()
    {
        StartedAt = execution.StartedAt,
        EndedAt = execution.EndedAt,
        EffectivePrincipal = execution.EffectivePrincipal ?? string.Empty,
        StatementId = execution.StatementId ?? string.Empty,
    };

    private static EvidenceAuthorizationClass ToMessage(Evidence.AuthorizationClass authorization) => new()
    {
        Name = authorization.Name ?? string.Empty,
        AccessLevel = authorization.AccessLevel,
        Compartments = { authorization.Compartments },
    };

    private static EvidenceRetention ToMessage(Evidence.Retention retention) => new()
    {
        ExpiresAt = retention.ExpiresAt ?? string.Empty,
        LegalHold = retention.LegalHold,
        PurgedAt = retention.PurgedAt ?? string.Empty,
    };

    /// <summary>Maps what a seal did onto the answer a client reads.</summary>
    internal static SealedEvidence ToMessage(WireSealResponse recorded) => new()
    {
        EvidenceId = recorded.EvidenceId,
        State = WireStateOf(recorded.State),
        Created = recorded.Created,
        Grant = recorded.Grant is { } grant
            ? new EvidenceUploadGrant { GrantId = grant.GrantId, ExpiresAt = grant.ExpiresAt }
            : null!,
    };

    /// <summary>Maps what a commit did onto the answer a client reads.</summary>
    internal static CommittedEvidence ToMessage(WireEvidenceCommit commit) => new()
    {
        EvidenceId = commit.EvidenceId,
        State = WireStateOf(commit.State),
        Committed = commit.Committed,
    };

    /// <summary>Maps what a purge did onto the answer a client reads.</summary>
    internal static PurgedEvidence ToMessage(WireEvidencePurge purge) => new()
    {
        EvidenceId = purge.EvidenceId,
        Purged = purge.Purged,
        State = WireStateOf(purge.State),
    };

    /// <summary>
    /// Maps one resolution onto the message a client reads.
    /// </summary>
    /// <remarks>
    /// The two row bounds are optional on the wire - a manifest read names no rows - and proto3 has no null for them, so
    /// an absent bound travels as zero. A resolution that read no rows and one that read from row zero are therefore the
    /// same message; the JSON surface keeps them apart, because JSON has null.
    /// </remarks>
    private static EvidenceAccess ToMessage(WireEvidenceAccess access) => new()
    {
        Uid = access.Uid,
        Kind = WireAccessKindOf(access.Kind),
        RowFrom = access.RowFrom ?? 0,
        RowLimit = access.RowLimit ?? 0,
        Outcome = WireOutcomeOf(access.Outcome),
        At = access.At,
    };

    /// <summary>
    /// Maps an artifact's resolutions onto the answer a client reads.
    /// </summary>
    internal static ListEvidenceAccessesResponse ToMessage(WireEvidenceAccessList list)
    {
        var accesses = new EvidenceAccesses { EvidenceId = list.EvidenceId };
        accesses.Accesses.AddRange(list.Accesses.Select(ToMessage));

        return new ListEvidenceAccessesResponse { Data = accesses };
    }

    /// <summary>
    /// Reads a part the contract declares required.
    /// </summary>
    /// <remarks>
    /// The kernel's manifest has required members and a proto3 message has no way to say so, so a caller that leaves one
    /// out is refused here, by name, rather than reaching the kernel as a null and failing there.
    /// </remarks>
    private static T Required<T>(T? part, string field)
        where T : class =>
        part ?? throw Refusal($"{field} is required.");

    /// <summary>
    /// Reads a string field, with the empty one meaning what the contract means by absent.
    /// </summary>
    /// <remarks>
    /// proto3 has no null string: an unset one reads as empty. The contract's own schema says absent means "not
    /// applicable to this kind" and never "unknown", and every string it carries is non-empty when it means something,
    /// so the empty one is the absent one.
    /// </remarks>
    private static string? Empty(string? value) => value is { Length: > 0 } ? value : null;

    /// <summary>
    /// Reads a number the contract leaves optional, with zero meaning absent.
    /// </summary>
    /// <remarks>
    /// A fidelity limit of proto3 rather than a choice: the generated field is a plain <c>long</c>, so a caller cannot
    /// send "absent" as distinct from zero. A scale of zero and a lag of zero seconds therefore read as absent, and a
    /// row read that starts at row zero reads as a read that named no rows. The JSON surface keeps the two apart,
    /// because JSON has null.
    /// </remarks>
    private static long? ZeroAsNone(long value) => value == 0 ? null : value;

    /// <summary>
    /// Refuses a call over gRPC for a reason the contract spells out.
    /// </summary>
    /// <remarks>
    /// A refusal here is always the caller's mistake in the request itself, so it is <c>INVALID_ARGUMENT</c> - the same
    /// code this surface answers a JSON invalid-request problem with.
    /// </remarks>
    private static RpcException Refusal(string detail) =>
        new(new Status(StatusCode.InvalidArgument, detail));

    /// <summary>Refuses a value the contract's closed vocabulary does not name.</summary>
    /// <remarks>
    /// A closed vocabulary is closed. Collapsing an unknown value onto a member would seal an artifact under a kind
    /// nobody meant, so the refusal names the field and the value instead.
    /// </remarks>
    private static RpcException Unsupported(string field, string value) =>
        Refusal($"{field} is not one of the contract's values: '{value}'.");

    // ---- the contract's closed vocabularies ------------------------------------------------------------------------
    //
    // Written out member by member rather than cast from one enum to the other. A cast would compile and would follow
    // whichever numbering each side happens to use, so a renumbering on either side would silently change what a sealed
    // artifact claims about itself. These are closed sets whose spellings are the contract's: a cast is exactly the kind
    // of quiet agreement this plane exists to avoid.

    /// <summary>Reads the canonicalization version, which this contract major pins to one member.</summary>
    private static string CanonOf(CanonEnum canon) => canon switch
    {
        CanonEnum.Canon1 => Evidence.EvidenceContract.Canon,
        _ => throw Unsupported("canon", canon.ToString()),
    };

    private static CanonEnum WireCanon(string canon) => canon switch
    {
        Evidence.EvidenceContract.Canon => CanonEnum.Canon1,
        _ => throw Unsupported("canon", canon),
    };

    private static Evidence.EvidenceKind KindOf(KindEnum kind) => kind switch
    {
        KindEnum.Table => Evidence.EvidenceKind.Table,
        KindEnum.Count => Evidence.EvidenceKind.Count,
        KindEnum.Observations => Evidence.EvidenceKind.Observations,
        _ => throw Unsupported("kind", kind.ToString()),
    };

    private static KindEnum WireKindOf(Evidence.EvidenceKind kind) => kind switch
    {
        Evidence.EvidenceKind.Table => KindEnum.Table,
        Evidence.EvidenceKind.Count => KindEnum.Count,
        Evidence.EvidenceKind.Observations => KindEnum.Observations,
        _ => throw Unsupported("kind", kind.ToString()),
    };

    private static string MediaTypeOf(MediaTypeEnum mediaType) => mediaType switch
    {
        MediaTypeEnum.ApplicationVndApacheParquet => Evidence.EvidenceContract.MediaTypeParquet,
        MediaTypeEnum.TextCsvCharsetUtf8 => Evidence.EvidenceContract.MediaTypeCsv,
        _ => throw Unsupported("media_type", mediaType.ToString()),
    };

    private static MediaTypeEnum WireMediaType(string mediaType) => mediaType switch
    {
        Evidence.EvidenceContract.MediaTypeParquet => MediaTypeEnum.ApplicationVndApacheParquet,
        Evidence.EvidenceContract.MediaTypeCsv => MediaTypeEnum.TextCsvCharsetUtf8,
        _ => throw Unsupported("media_type", mediaType),
    };

    private static Evidence.RowIdRule RowIdRuleOf(RowIdRuleEnum rule) => rule switch
    {
        RowIdRuleEnum.Keys => Evidence.RowIdRule.Keys,
        RowIdRuleEnum.Position => Evidence.RowIdRule.Position,
        _ => throw Unsupported("identity.row_id_rule", rule.ToString()),
    };

    private static RowIdRuleEnum WireRowIdRule(Evidence.RowIdRule rule) => rule switch
    {
        Evidence.RowIdRule.Keys => RowIdRuleEnum.Keys,
        Evidence.RowIdRule.Position => RowIdRuleEnum.Position,
        _ => throw Unsupported("identity.row_id_rule", rule.ToString()),
    };

    private static Evidence.ColumnType ColumnTypeOf(TypeEnum type) => type switch
    {
        TypeEnum.Bool => Evidence.ColumnType.Bool,
        TypeEnum.Int64 => Evidence.ColumnType.WholeNumber,
        TypeEnum.Decimal => Evidence.ColumnType.ExactDecimal,
        TypeEnum.Float64 => Evidence.ColumnType.RealNumber,
        TypeEnum.String => Evidence.ColumnType.Text,
        TypeEnum.Bytes => Evidence.ColumnType.Bytes,
        TypeEnum.Date => Evidence.ColumnType.Date,
        TypeEnum.TimestampTz => Evidence.ColumnType.TimestampTz,
        TypeEnum.TimestampNaive => Evidence.ColumnType.TimestampNaive,
        TypeEnum.Interval => Evidence.ColumnType.Interval,
        TypeEnum.Uuid => Evidence.ColumnType.Uuid,
        TypeEnum.Json => Evidence.ColumnType.Json,
        TypeEnum.Array => Evidence.ColumnType.Array,
        _ => throw Unsupported("schema.columns[].type", type.ToString()),
    };

    private static TypeEnum WireColumnType(Evidence.ColumnType type) => type switch
    {
        Evidence.ColumnType.Bool => TypeEnum.Bool,
        Evidence.ColumnType.WholeNumber => TypeEnum.Int64,
        Evidence.ColumnType.ExactDecimal => TypeEnum.Decimal,
        Evidence.ColumnType.RealNumber => TypeEnum.Float64,
        Evidence.ColumnType.Text => TypeEnum.String,
        Evidence.ColumnType.Bytes => TypeEnum.Bytes,
        Evidence.ColumnType.Date => TypeEnum.Date,
        Evidence.ColumnType.TimestampTz => TypeEnum.TimestampTz,
        Evidence.ColumnType.TimestampNaive => TypeEnum.TimestampNaive,
        Evidence.ColumnType.Interval => TypeEnum.Interval,
        Evidence.ColumnType.Uuid => TypeEnum.Uuid,
        Evidence.ColumnType.Json => TypeEnum.Json,
        Evidence.ColumnType.Array => TypeEnum.Array,
        _ => throw Unsupported("schema.columns[].type", type.ToString()),
    };

    /// <summary>Reads an artifact's state, which the operations report as the contract's name for it.</summary>
    private static StateEnum WireStateOf(string state) =>
        Evidence.EvidenceStateNames.ParseState(state) switch
        {
            Evidence.EvidenceState.Pending => StateEnum.Pending,
            Evidence.EvidenceState.Committed => StateEnum.Committed,
            Evidence.EvidenceState.Purged => StateEnum.Purged,
            _ => throw Unsupported("state", state),
        };

    /// <summary>Reads what a resolution was about.</summary>
    private static EvidenceAccessKind WireAccessKindOf(string kind) => kind switch
    {
        "manifest" => EvidenceAccessKind.Manifest,
        "rows" => EvidenceAccessKind.Rows,
        _ => throw Unsupported("kind", kind),
    };

    /// <summary>Reads how a resolution went.</summary>
    private static OutcomeEnum WireOutcomeOf(string outcome) => outcome switch
    {
        "ok" => OutcomeEnum.Ok,
        "denied" => OutcomeEnum.Denied,
        "expired" => OutcomeEnum.Expired,
        _ => throw Unsupported("outcome", outcome),
    };

    /// <summary>Refuses a call that omitted a part the contract requires.</summary>
    internal static RpcException Missing(string field) => Refusal($"{field} is required.");
}
