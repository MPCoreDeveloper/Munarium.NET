namespace Munarium.Server;

using Microsoft.Extensions.DependencyInjection;
using Munarium.Claims;
using Munarium.Context;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Retrieval;
using Munarium.Shapes;
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

    private readonly ServiceProvider _provider;
    private readonly IDatabase _database;
    private readonly SharpCoreDbRetriever _retriever;
    private readonly DeterministicEmbeddingProvider _embedder;

    // Internal rather than public: a kernel is composed through Create, so a caller cannot build one
    // without the ledger, the shapes and the retriever that make it work.
    internal MunariumKernel(
        ServiceProvider provider,
        IDatabase database,
        SharpCoreDbRetriever retriever,
        DeterministicEmbeddingProvider embedder,
        MunariumOperations operations,
        ShapeRegistry shapes)
    {
        _provider = provider;
        _database = database;
        _retriever = retriever;
        _embedder = embedder;
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
        var retriever = new SharpCoreDbRetriever(EmbeddingDimensions, "munarium@1", new SequenceNumber(0));

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

        var operations = new MunariumOperations(
            storage,
            claims,
            candidates,
            findings,
            facts,
            shapes,
            retriever,
            embedder,
            new Composer(facts),
            snapshots,
            DeterministicEmbeddingProvider.ModelName);

        return new MunariumKernel(provider, database, retriever, embedder, operations, shapes);
    }

    /// <summary>
    /// Indexes one document chunk, which is what the retrieval leg reads.
    /// </summary>
    /// <remarks>
    /// Ingestion is not on the wire yet - this is the seam the ingest surface will land on, kept here
    /// so the retrieval path in a server can be exercised rather than only described.
    /// </remarks>
    /// <param name="source">Where the chunk came from.</param>
    /// <param name="text">The chunk text.</param>
    public void Index(SourceReference source, string text)
    {
        ArgumentNullException.ThrowIfNull(source);

        _retriever.Index(source, text, _embedder.Embed(text));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _retriever.Dispose();
        await _database.DisposeAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
