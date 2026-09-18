namespace Munarium.Store.SharpCoreDb;

using System.Globalization;
using Munarium.Evidence;
using SharpCoreDB.Interfaces;

/// <summary>
/// The evidence plane, held in SharpCoreDB tables: the artifacts, the grants issued for them, and the record of who
/// resolved one.
/// </summary>
/// <remarks>
/// Three tables rather than one, for the reason the source slice gives for its two: they change at different rates. An
/// artifact is written once and rewritten only when its lifecycle moves; a grant lives for fifteen minutes and is spent
/// at most once; an access is appended and read only by an auditor. One table would put the rarest writes and the most
/// frequent ones in the same place.
/// <para>
/// An artifact is addressed by identity - <c>evidence_id</c>, which the server assigns at seal, and <c>domain_key</c>,
/// the hash of the tuple that makes two seals one seal - because identity is the only thing a predicate may be built
/// over: measured on 2.1.0-RC.3, <em>comparing</em> caller-supplied text matches nothing, so a lookup by tenant would
/// answer "no such artifact" for a row that is sitting right there. Tenant and state are therefore checked after the
/// read, which is the rule every table in this package follows.
/// </para>
/// <para>
/// The lifecycle facts stay in the manifest instead of being copied into columns beside it. An expiry, a hold and a
/// purge instant are properties of what the artifact proves, and a second copy of them is a second thing that can
/// disagree with it. A sweep therefore reads the rows and judges them, which is honest at the sizes retention runs over
/// and is the thing to revisit when a deployment measures otherwise.
/// </para>
/// <para>
/// The manifest is stored with its <c>evidence_id</c> filled in, because the manifest's own contract says the id is
/// absent in a seal request and present in every read: writing the caller's manifest unchanged would leave a row whose
/// id is only in a column, and a reader holding the manifest alone could not say which artifact it describes.
/// </para>
/// </remarks>
/// <param name="database">The database the plane lives in.</param>
/// <param name="tablePrefix">The prefix of the three tables.</param>
public sealed class SharpCoreDbEvidenceStore(
    IDatabase database,
    string tablePrefix = SharpCoreDbEvidenceStore.DefaultTablePrefix) : IEvidenceStore
{
    /// <summary>
    /// The table prefix a deployment gets when it does not choose one.
    /// </summary>
    /// <remarks>
    /// The names are the original's - <c>evidence_artifacts</c>, <c>evidence_grants</c>, <c>evidence_access</c> - so an
    /// operator moving between the two deployments reads the same tables.
    /// </remarks>
    public const string DefaultTablePrefix = "evidence";

    // The columns are the original's, including the ones lifted out of the manifest. The reason it gives for lifting
    // them is the reason they are here: the JSON is the record, and the columns are what retention and authorization are
    // decided on, because a decision must never depend on a JSON path lookup that a malformed document could bend. They
    // are written from one artifact in one call, so a column can only disagree with the manifest if a row were edited
    // by hand.
    //
    // Two columns are spelled differently from the original's, and both are the engine's doing rather than a choice:
    // there is no timestamp type here, so instants are RFC 3339 text; and there is no array type, so compartments are
    // joined with the unit separator the contract's identity material already uses, which cannot appear in a tag.
    private const string ArtifactSchema =
        "tenant_id TEXT, evidence_id TEXT, domain_key TEXT, state TEXT, kind TEXT, media_type TEXT, bytes_len LONG, "
        + "logical_result_hash TEXT, artifact_hash TEXT, access_level LONG, compartments TEXT, expires_at TEXT, "
        + "legal_hold LONG, purged_at TEXT, blob_path TEXT, created_at TEXT, committed_at TEXT, manifest TEXT";

    // No created_at: the original's table carries one because its grant record does, and this port's EvidenceGrant has no
    // such field - a column no reader can get back is a column nobody can check.
    private const string GrantSchema =
        "tenant_id TEXT, grant_id TEXT, evidence_id TEXT, expires_at TEXT, used_at TEXT";

    // No identity of its own: an access is read as a list, newest first, and the original orders that list by the instant
    // the read happened rather than by an id this adapter would have to mint.
    private const string AccessSchema =
        "tenant_id TEXT, evidence_id TEXT, uid TEXT, kind TEXT, row_from LONG, row_limit LONG, outcome TEXT, at TEXT";

    /// <summary>
    /// What joins the compartments in their column.
    /// </summary>
    /// <remarks>
    /// The unit separator: the same character the contract's domain-key material uses, and for the same reason. Nothing
    /// validates a compartment tag against it, which is what makes a join on it unambiguous - and a join that could be
    /// read two ways would be a class two artifacts could share.
    /// </remarks>
    private const string UnitSeparator = "\u001f";

    private readonly Lock _gate = new();
    private readonly IDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly (string Name, string Schema) _artifacts =
        (TableValues.ValidateTableName(tablePrefix + "_artifacts"), ArtifactSchema);
    private readonly (string Name, string Schema) _grants =
        (TableValues.ValidateTableName(tablePrefix + "_grants"), GrantSchema);
    private readonly (string Name, string Schema) _accesses =
        (TableValues.ValidateTableName(tablePrefix + "_accesses"), AccessSchema);

    /// <inheritdoc />
    public ValueTask<SealOutcome> RegisterAsync(
        EvidenceArtifact artifact,
        EvidenceGrant? grant = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();

        // A grant is a capability to upload bytes for an artifact that does not have them yet. Issuing one for an
        // artifact the caller also declares committed would authorize a write to something already sealed, so the two are
        // refused together rather than resolved by a guess about which one the caller meant.
        if (grant is not null && artifact.State != EvidenceState.Pending)
        {
            throw new ArgumentException(
                $"a grant authorizes the bytes of a pending artifact, and this one is {artifact.State.ToWireName()}",
                nameof(grant));
        }

        lock (_gate)
        {
            var artifacts = Table(_artifacts);
            var domainKey = artifact.Manifest.ComputeDomainKey();
            var existing = artifacts
                .Select(TableValues.Identity("domain_key", domainKey))
                .Select(MapArtifact)
                .FirstOrDefault(found => string.Equals(found.Tenant, artifact.Tenant, StringComparison.Ordinal));

            // The domain key is the idempotency tuple, so this is the same seal: what is recorded stays recorded - a
            // caller arriving with different bytes under the same key cannot rewrite what an artifact is - and no grant
            // is issued, because the bytes a grant would authorize already exist.
            if (existing is not null)
            {
                return ValueTask.FromResult(new SealOutcome { EvidenceId = existing.EvidenceId, Created = false });
            }

            WriteArtifact(artifacts, artifact);

            if (grant is not null)
            {
                WriteGrant(Table(_grants), grant);
            }

            Persist();

            return ValueTask.FromResult(new SealOutcome
            {
                EvidenceId = artifact.EvidenceId,
                Created = true,
                Grant = grant,
            });
        }
    }

    /// <inheritdoc />
    public ValueTask<EvidenceArtifact?> GetAsync(
        string tenant,
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(Read(Table(_artifacts), tenant, evidenceId));
        }
    }

    /// <inheritdoc />
    public ValueTask<EvidenceArtifact?> FindByDomainKeyAsync(
        string tenant,
        string domainKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(
                Table(_artifacts)
                    .Select(TableValues.Identity("domain_key", domainKey))
                    .Select(MapArtifact)
                    .FirstOrDefault(found => string.Equals(found.Tenant, tenant, StringComparison.Ordinal)));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> CommitAsync(
        string tenant,
        string evidenceId,
        string at,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var artifacts = Table(_artifacts);
            var found = Read(artifacts, tenant, evidenceId);

            // Nothing moves unless the artifact is pending. One already committed is the replay this answer exists to
            // report; one whose bytes are purged stays purged, because a late commit cannot un-delete anything and
            // calling it committed is the lie the surviving row exists to prevent; and one that was never registered has
            // nothing to commit.
            if (found is null || found.State != EvidenceState.Pending)
            {
                return ValueTask.FromResult(false);
            }

            WriteArtifact(artifacts, found with { State = EvidenceState.Committed, CommittedAt = at });
            Persist();

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<EvidenceGrant?> ConsumeGrantAsync(
        string tenant,
        string evidenceId,
        string grantId,
        string now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(grantId))
            {
                return ValueTask.FromResult<EvidenceGrant?>(null);
            }

            var grants = Table(_grants);
            var row = grants
                .Select(TableValues.Identity("grant_id", grantId))
                .FirstOrDefault(found =>
                    string.Equals(TableValues.StringValue(found, "tenant_id"), tenant, StringComparison.Ordinal)
                    && string.Equals(TableValues.StringValue(found, "evidence_id"), evidenceId, StringComparison.Ordinal));

            // Reading the row and spending it are one step under one lock, which is the whole reason this lives in the
            // store rather than in a caller doing both: a single-use check split in two is a check two requests can pass.
            if (row is null)
            {
                return ValueTask.FromResult<EvidenceGrant?>(null);
            }

            var expiresAt = TableValues.StringValue(row, "expires_at");

            // A grant whose expiry cannot be read cannot be shown to be usable, and the clock the caller passes is the
            // only clock there is - the plane does not keep one of its own to disagree with it.
            if (TableValues.StringValue(row, "used_at").Length > 0
                || !Instant(expiresAt, out var expiry)
                || !Instant(now, out var clock)
                || clock >= expiry)
            {
                return ValueTask.FromResult<EvidenceGrant?>(null);
            }

            var spent = new EvidenceGrant
            {
                GrantId = grantId,
                EvidenceId = evidenceId,
                Tenant = tenant,
                ExpiresAt = expiresAt,
                UsedAt = now,
            };

            WriteGrant(grants, spent);
            Persist();

            return ValueTask.FromResult<EvidenceGrant?>(spent);
        }
    }

    /// <inheritdoc />
    public ValueTask RecordAccessAsync(EvidenceAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Table(_accesses).Insert(new Dictionary<string, object>
            {
                // What was read, never the rows: an audit table holding the regulated data it audits would be a second
                // copy of the problem the audit exists to describe.
                ["tenant_id"] = access.Tenant,
                ["evidence_id"] = access.EvidenceId,
                ["uid"] = access.Uid,
                ["kind"] = access.Kind,
                ["row_from"] = access.RowFrom ?? 0L,
                ["row_limit"] = access.RowLimit ?? 0L,
                ["outcome"] = access.Outcome,
                ["at"] = access.At,
            });

            Persist();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<EvidenceAccess>> AccessesAsync(
        string tenant,
        string evidenceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(Recent(tenant, evidenceId, limit));
        }
    }

    /// <summary>
    /// An artifact's recent accesses, newest first.
    /// </summary>
    /// <remarks>
    /// Ordered by the instant each read happened, as the original orders it, and over the parsed value rather than over
    /// the text: this engine has no timestamp type, and two spellings of one instant would sort differently as text.
    /// A row whose instant cannot be read sorts after the ones that can, which is the honest end for it.
    /// </remarks>
    private IReadOnlyList<EvidenceAccess> Recent(string tenant, string evidenceId, int limit) =>
        limit <= 0 || string.IsNullOrWhiteSpace(evidenceId)
            ? []
            : [
                .. Table(_accesses)
                    .Select(TableValues.Identity("evidence_id", evidenceId))
                    .Where(row => string.Equals(
                        TableValues.StringValue(row, "tenant_id"), tenant, StringComparison.Ordinal))
                    .OrderByDescending(row => Instant(TableValues.StringValue(row, "at"), out var at)
                        ? at
                        : (DateTimeOffset?)null)
                    .Take(limit)
                    .Select(MapAccess),
            ];

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<EvidenceArtifact>> PurgeDueAsync(
        string now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (limit <= 0 || !Instant(now, out var clock))
            {
                return ValueTask.FromResult<IReadOnlyList<EvidenceArtifact>>([]);
            }

            // Every tenant, on purpose: the janitor is a deployment-wide obligation, and a sweep that ran for the tenants
            // somebody remembered would not be a retention policy. Which rows are due is decided on the columns rather
            // than out of the manifests, so the sweep does not parse a document per artifact to find the handful it
            // wants; only the rows it is about to hand back are read as artifacts.
            return ValueTask.FromResult<IReadOnlyList<EvidenceArtifact>>(
            [
                .. Table(_artifacts)
                    .Select()
                    .Select(row => (Row: row, Due: DueAt(row)))
                    .Where(candidate => candidate.Due is { } expires && clock >= expires)
                    .OrderBy(candidate => candidate.Due)
                    .ThenBy(candidate => TableValues.StringValue(candidate.Row, "evidence_id"), StringComparer.Ordinal)
                    .Take(limit)
                    .Select(candidate => MapArtifact(candidate.Row)),
            ]);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkPurgedAsync(
        string tenant,
        string evidenceId,
        string at,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var artifacts = Table(_artifacts);
            var found = Read(artifacts, tenant, evidenceId);
            var retention = found?.Manifest.Retention;

            // Only committed bytes can be purged, and a hold stops it - both read from the columns through the mapping,
            // which is where the decision is taken rather than out of the manifest. The listing already leaves holds out,
            // so this is a second gate rather than the only one - but a janitor that marked a held artifact purged would
            // be asserting a deletion the hold forbade, and a hold that stops nothing is not a hold.
            if (found is null || found.State != EvidenceState.Committed || retention is { LegalHold: true })
            {
                return ValueTask.FromResult(false);
            }

            WriteArtifact(artifacts, found with
            {
                State = EvidenceState.Purged,
                Manifest = found.Manifest with
                {
                    Retention = (retention ?? new Retention()) with { PurgedAt = at },
                },
            });
            Persist();

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> SetLegalHoldAsync(
        string tenant,
        string evidenceId,
        bool hold,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var artifacts = Table(_artifacts);
            var found = Read(artifacts, tenant, evidenceId);

            if (found is null)
            {
                return ValueTask.FromResult(false);
            }

            WriteArtifact(artifacts, found with
            {
                Manifest = found.Manifest with
                {
                    // A hold on an artifact sealed without retention is still a hold: an instruction to preserve evidence
                    // does not depend on the producer having declared when it would have expired.
                    Retention = (found.Manifest.Retention ?? new Retention()) with { LegalHold = hold },
                },
            });
            Persist();

            return ValueTask.FromResult(true);
        }
    }

    private static EvidenceArtifact? Read(ITable table, string tenant, string evidenceId) =>
        string.IsNullOrWhiteSpace(evidenceId)
            ? null
            : table
                .Select(TableValues.Identity("evidence_id", evidenceId))
                .Select(MapArtifact)
                .FirstOrDefault(artifact => string.Equals(artifact.Tenant, tenant, StringComparison.Ordinal));

    private static void WriteArtifact(ITable table, EvidenceArtifact artifact)
    {
        var manifest = artifact.Manifest with { EvidenceId = artifact.EvidenceId };
        var retention = manifest.Retention;

        table.Delete(TableValues.Identity("evidence_id", artifact.EvidenceId));
        table.Insert(new Dictionary<string, object>
        {
            ["tenant_id"] = artifact.Tenant,
            ["evidence_id"] = artifact.EvidenceId,
            ["domain_key"] = manifest.ComputeDomainKey(),
            ["state"] = artifact.State.ToWireName(),
            ["kind"] = manifest.Kind.ToWireName(),
            ["media_type"] = manifest.MediaType,
            ["bytes_len"] = manifest.BytesLength,
            ["logical_result_hash"] = manifest.LogicalResultHash,
            ["artifact_hash"] = manifest.ArtifactHash,
            ["access_level"] = (long)manifest.AuthorizationClass.AccessLevel,
            ["compartments"] = string.Join(UnitSeparator, manifest.AuthorizationClass.Compartments),
            ["expires_at"] = retention?.ExpiresAt ?? string.Empty,
            ["legal_hold"] = retention?.LegalHold == true ? 1L : 0L,
            ["purged_at"] = retention?.PurgedAt ?? string.Empty,
            ["blob_path"] = artifact.BlobPath,
            ["created_at"] = artifact.CreatedAt,
            ["committed_at"] = artifact.CommittedAt ?? string.Empty,
            ["manifest"] = EvidenceManifestCodec.ToJson(manifest),
        });
    }

    private static void WriteGrant(ITable table, EvidenceGrant grant)
    {
        table.Delete(TableValues.Identity("grant_id", grant.GrantId));
        table.Insert(new Dictionary<string, object>
        {
            ["tenant_id"] = grant.Tenant,
            ["grant_id"] = grant.GrantId,
            ["evidence_id"] = grant.EvidenceId,
            ["expires_at"] = grant.ExpiresAt,
            ["used_at"] = grant.UsedAt ?? string.Empty,
        });
    }

    private static EvidenceArtifact MapArtifact(Dictionary<string, object> row)
    {
        var evidenceId = TableValues.StringValue(row, "evidence_id");
        var manifest = EvidenceManifestCodec.FromJson(
            TableValues.StringValue(row, "manifest"),
            $"artifact '{evidenceId}'");

        // The columns win over the manifest for everything a decision is made on - what kind of result this is, its two
        // hashes and its length, who may read it, and when it goes. That is what lifting them out of the JSON is for: the
        // decision is taken on a column rather than on a path through a document. The manifest keeps the rest, including
        // the identity, which is filled in here for the same reason the store fills it in on the way down.
        return new EvidenceArtifact
        {
            EvidenceId = evidenceId,
            Tenant = TableValues.StringValue(row, "tenant_id"),
            State = EvidenceStateNames.ParseState(TableValues.StringValue(row, "state"))
                ?? throw new FormatException($"artifact '{evidenceId}' is in a state this server does not know"),
            Manifest = manifest with
            {
                EvidenceId = evidenceId,
                Kind = Kind(row, evidenceId),
                MediaType = TableValues.StringValue(row, "media_type"),
                BytesLength = TableValues.LongValue(row, "bytes_len"),
                LogicalResultHash = TableValues.StringValue(row, "logical_result_hash"),
                ArtifactHash = TableValues.StringValue(row, "artifact_hash"),
                AuthorizationClass = new AuthorizationClass
                {
                    Name = manifest.AuthorizationClass.Name,
                    AccessLevel = (int)TableValues.LongValue(row, "access_level"),
                    Compartments = Compartments(row),
                },
                Retention = Retention(row),
            },
            BlobPath = TableValues.StringValue(row, "blob_path"),
            CreatedAt = TableValues.StringValue(row, "created_at"),
            CommittedAt = Moment(row, "committed_at"),
        };
    }

    private static EvidenceKind Kind(Dictionary<string, object> row, string evidenceId) =>
        EvidenceKindNames.ParseKind(TableValues.StringValue(row, "kind"))
            ?? throw new FormatException($"artifact '{evidenceId}' is of a kind this server does not know");

    private static IReadOnlyList<string> Compartments(Dictionary<string, object> row) =>
        TableValues.StringValue(row, "compartments") is { Length: > 0 } joined
            ? joined.Split(UnitSeparator, StringSplitOptions.None)
            : [];

    /// <summary>Reads the retention the columns carry, which is the copy a purge is decided on.</summary>
    private static Retention? Retention(Dictionary<string, object> row)
    {
        var expiresAt = Moment(row, "expires_at");
        var purgedAt = Moment(row, "purged_at");
        var held = TableValues.LongValue(row, "legal_hold") != 0;

        return expiresAt is null && purgedAt is null && !held
            ? null
            : new Retention { ExpiresAt = expiresAt, LegalHold = held, PurgedAt = purgedAt };
    }

    private static EvidenceAccess MapAccess(Dictionary<string, object> row) => new()
    {
        EvidenceId = TableValues.StringValue(row, "evidence_id"),
        Tenant = TableValues.StringValue(row, "tenant"),
        Uid = TableValues.StringValue(row, "uid"),
        Kind = TableValues.StringValue(row, "kind"),
        RowFrom = Rows(row, "row_from"),
        RowLimit = Rows(row, "row_limit"),
        Outcome = TableValues.StringValue(row, "outcome"),
        At = TableValues.StringValue(row, "at"),
    };

    private static string? Moment(Dictionary<string, object> row, string column) =>
        TableValues.StringValue(row, column) is { Length: > 0 } stamp ? stamp : null;

    /// <summary>
    /// Reads a row count the caller named, or <see langword="null"/> when there was none.
    /// </summary>
    /// <remarks>
    /// Reading from the first row and not naming a first row say the same thing about a read, so zero is stored for both
    /// and read back as none: a column that had to hold the difference would need a marker rather than a number.
    /// </remarks>
    private static long? Rows(Dictionary<string, object> row, string column)
    {
        var value = TableValues.LongValue(row, column);

        return value > 0 ? value : null;
    }

    /// <summary>Gets when a row's bytes become deletable, or <see langword="null"/> while they may not be.</summary>
    /// <remarks>
    /// Decided on the columns, which is the point of keeping them: only a committed artifact whose retention has come
    /// round and which is not on hold is due, and an expiry nobody can read is not one this will act on - deleting
    /// regulated bytes on a guess is the one direction this cannot be wrong in.
    /// </remarks>
    private static DateTimeOffset? DueAt(Dictionary<string, object> row) =>
        EvidenceStateNames.ParseState(TableValues.StringValue(row, "state")) is EvidenceState.Committed
        && TableValues.LongValue(row, "legal_hold") == 0
        && TableValues.StringValue(row, "purged_at").Length == 0
        && Instant(TableValues.StringValue(row, "expires_at"), out var expires)
            ? expires
            : null;

    private static bool Instant(string text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);

    private ITable Table((string Name, string Schema) table)
    {
        if (_database.TryGetTable(table.Name, out var existing))
        {
            return existing;
        }

        _database.ExecuteSQL($"CREATE TABLE {table.Name} ({table.Schema})");

        return _database.TryGetTable(table.Name, out var created)
            ? created
            : throw new InvalidOperationException(
                $"the table '{table.Name}' could not be created, so no evidence can be kept");
    }

    private void Persist()
    {
        _database.Flush();
        _database.ForceSave();
    }
}
