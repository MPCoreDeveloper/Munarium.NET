namespace Munarium.Server;

using Microsoft.Extensions.DependencyInjection;
using Munarium.Claims;
using Munarium.Context;
using Munarium.Counters;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Promises;
using Munarium.Retrieval;
using Munarium.Shapes;
using Munarium.Sources;
using Munarium.Store.SharpCoreDb;
using Munarium.Wire;
using SharpCoreDB;
using SharpCoreDB.EventSourcing;
using SharpCoreDB.Interfaces;

/// <summary>
/// A composed Munarium: the ledger, the shapes, the retriever and the one operation surface.
/// </summary>
/// <remarks>
/// Composition lives here rather than in <c>Program.cs</c> so a test builds exactly the kernel a
/// server builds - and disposes it the same way - instead of approximating it.
/// </remarks>
public sealed class MunariumKernel : IAsyncDisposable
{
    private const int EmbeddingDimensions = 256;

    /// <summary>
    /// The tenant a deployment's sources belong to.
    /// </summary>
    /// <remarks>
    /// One deployment, one tenant, until authorization and tenancy land: the kernel's seams are already tenant-keyed -
    /// a source id is derived from the tenant and the path - so tenancy is a matter of threading a caller's tenant
    /// through rather than reshaping anything. Composed here rather than invented per request, because a tenant that
    /// varied by request would put one deployment's documents in several tenants' stores.
    /// </remarks>
    public const string Tenant = "default";

    /// <summary>
    /// The index version a deployment starts by serving.
    /// </summary>
    /// <remarks>
    /// Named rather than derived, and deliberately not an <c>idx-</c> identity: this version was composed at startup and
    /// not built from recorded sources, so no manifest describes it. An answer from it therefore cites a version the
    /// catalogue cannot resolve - which is a true statement about an index nobody built, and the honest one to make
    /// until a build records a version that can be verified.
    /// </remarks>
    public const string InitialIndexVersion = "munarium@1";

    private readonly ServiceProvider _provider;
    private readonly IDatabase _database;
    private readonly SharpCoreDbIndexHost _host;
    private readonly IndexBuilder _builder;
    private readonly IndexCatalog _catalogue;
    private readonly FactLedger _facts;

    // Internal rather than public: a kernel is composed through Create, so a caller cannot build one
    // without the ledger, the shapes and the index host that make it work.
    internal MunariumKernel(
        ServiceProvider provider,
        IDatabase database,
        SharpCoreDbIndexHost host,
        IndexBuilder builder,
        IndexCatalog catalogue,
        FactLedger facts,
        MunariumOperations operations,
        ShapeRegistry shapes)
    {
        _provider = provider;
        _database = database;
        _host = host;
        _builder = builder;
        _catalogue = catalogue;
        _facts = facts;
        Operations = operations;
        Shapes = shapes;
    }

    /// <summary>Gets the one implementation behind both transports.</summary>
    public MunariumOperations Operations { get; }

    /// <summary>Gets the shapes this kernel was composed with.</summary>
    public ShapeRegistry Shapes { get; }

    /// <summary>
    /// Composes a kernel over an embedded SharpCoreDB database.
    /// </summary>
    /// <param name="databasePath">Where the ledger lives.</param>
    /// <param name="databaseName">The database name.</param>
    /// <param name="shapes">The shapes this deployment understands.</param>
    /// <returns>The composed kernel.</returns>
    public static MunariumKernel Create(string databasePath, string databaseName, ShapeRegistry shapes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(shapes);

        var provider = new ServiceCollection().AddSharpCoreDB().BuildServiceProvider();
        var database = provider.GetRequiredService<DatabaseFactory>().Create(databasePath, databaseName);
        var storage = new SharpCoreDbStorageBackend(new SharpCoreDbEventStore(database));

        var embedder = new DeterministicEmbeddingProvider(EmbeddingDimensions);

        // One host holds every index instance this process has: the version it starts by serving, and the ones a build
        // creates. The version it starts with is composed rather than built - nobody derived it from recorded sources -
        // so an answer from it cites a version the catalogue does not know, which is exactly what provenance resolution
        // reports until a build records one.
        var host = new SharpCoreDbIndexHost(EmbeddingDimensions, InitialIndexVersion, SequenceNumber.Zero);

        // The conflict gate reads the ledger, so it is built over the read model rather than over the
        // command being judged: that is the whole point of judging a write against what is already there.
        var facts = new FactLedger(storage);
        var claims = new ClaimLedger(storage, shapes, [new ShapeGate(shapes), new LedgerConflictGate(shapes, facts)]);

        // One snapshot builder serves both the write path and the audit read, so a gate and a caller reading a
        // snapshot are looking at the same construction of the same pin.
        var snapshots = new MeshSnapshotBuilder(storage);

        // The candidate plane: a batch judged as one unit against the head the gates read, which is what the
        // claim-batch operation carries. It reads the same ledger through the same storage seam.
        var candidates = new CandidateLedger(storage, snapshots);

        // Findings are read back out of the same stream the write path records them in, so the two cannot be out
        // of step: there is no second table to disagree with.
        var findings = new FindingsLedger(storage);

        // The authoring paths for the two keyed planes a snapshot reads: a lock, and a promise one scope owes to
        // another. Both read the plane they write through the same builder, so a release or a fulfilment answers
        // from what is actually there rather than from what the caller believed.
        var anchors = new AnchorLedger(storage, snapshots);
        var promises = new PromiseLedger(storage, snapshots);

        // A counter needs no pre-state: it is an absolute total and the plane keys it by the pattern, so recording
        // one is an upsert rather than a comparison against what is there.
        var counters = new CounterLedger(storage);

        // The ingest slice: the source store and its rows, and the runner that takes a document from bytes to indexed
        // chunks through the same retriever the search operation reads. The deployment is one tenant until authorization
        // lands, and its name is composed here rather than guessed per request.
        var sourceStore = new SharpCoreDbSourceStore(database);
        var sourceRegistry = new SharpCoreDbSourceRegistry(database);
        var ingest = new IngestRunner(
            new SourceIngest(sourceStore, sourceRegistry),
            embedder,
            host,
            DeterministicEmbeddingProvider.ModelName);

        // The index-version table and the rule layer over it, and the builder that fills a version from the rows. The
        // engine reference comes from the host, so a manifest cannot claim an engine that did not build the vectors.
        var versionStore = new SharpCoreDbIndexVersionStore(database);
        var catalogue = new IndexCatalog(versionStore);
        var builder = new IndexBuilder(
            sourceStore,
            sourceRegistry,
            embedder,
            host,
            catalogue,
            new EmbedderRef(
                ProviderId.Local.Value,
                DeterministicEmbeddingProvider.ModelName,
                EmbeddingDimensions));

        var operations = new MunariumOperations(
            storage,
            claims,
            candidates,
            findings,
            anchors,
            promises,
            counters,
            facts,
            shapes,
            host,
            embedder,
            new Composer(facts),
            snapshots,
            DeterministicEmbeddingProvider.ModelName,
            ingest,
            sourceRegistry,
            builder,
            catalogue,
            Tenant);

        return new MunariumKernel(provider, database, host, builder, catalogue, facts, operations, shapes);
    }

    /// <summary>
    /// Rebuilds every live version this deployment has, and serves what it can.
    /// </summary>
    /// <remarks>
    /// What a restart needs: the chunks of an index live in the process that built them, so a deployment that comes back
    /// has the rows and the version records and no index. Each live version names the prefix it was built from, so the
    /// rebuild reads the same corpus and mints the same identity - the same version, rebuilt - while a corpus that
    /// changed in the meantime rebuilds into a version that includes the change, with the previous one still resolvable.
    /// <para>
    /// A version that cannot be rebuilt is reported rather than skipped: the deployment keeps serving whatever it was
    /// serving, and the answer says which corpus could not come back. The report is cheap; a deployment that quietly
    /// answered from an empty index would not be.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One line per live version: the version that serves now, or why nothing does.</returns>
    public async ValueTask<IReadOnlyList<IndexRecovery>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var live = await _catalogue.ListActiveAsync(Tenant, cancellationToken).ConfigureAwait(false);
        var recovered = new List<IndexRecovery>(live.Count);

        foreach (var version in live)
        {
            var outcome = await _builder
                .BuildAsync(
                    new IndexBuildPlan
                    {
                        Tenant = Tenant,
                        CollectionId = version.CollectionId,
                        CollectionName = version.Manifest.CollectionName,
                        ShapeRef = version.ShapeRef,
                        PathPrefix = version.PathPrefix,
                        Watermark = await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false),
                        Activate = true,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            recovered.Add(outcome switch
            {
                IndexVersion rebuilt => new IndexRecovery(version.CollectionId, rebuilt.Id, Refusal: null),
                IndexBuildRefused refused => new IndexRecovery(version.CollectionId, version.Id, refused.Reason),
            });
        }

        return recovered;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        await _database.DisposeAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// What a deployment did with one live index version when it started.
/// </summary>
/// <param name="CollectionId">The collection whose version was rebuilt.</param>
/// <param name="IndexVersionId">The version that serves now: the same one, or the new one the corpus rebuilt into.</param>
/// <param name="Refusal">Why nothing serves, or <see langword="null"/> when the rebuild landed.</param>
public sealed record IndexRecovery(string CollectionId, string IndexVersionId, string? Refusal);
