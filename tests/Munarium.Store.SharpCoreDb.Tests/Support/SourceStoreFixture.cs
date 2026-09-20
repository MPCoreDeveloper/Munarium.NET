namespace Munarium.Store.SharpCoreDb.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using SharpCoreDB;
using SharpCoreDB.Interfaces;

/// <summary>
/// A real SharpCoreDB database with the two source adapters over it, disposed together.
/// </summary>
/// <remarks>
/// A real database rather than a double, because what these adapters have to get right is the engine's behaviour: how
/// a quote survives an insert, whether a re-put replaces a row, and whether a prefix filter over-matched. A fake table
/// would agree with whatever the adapter believed.
/// </remarks>
internal sealed class SourceStoreFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IDatabase _database;

    private SourceStoreFixture(ServiceProvider provider, IDatabase database, string databasePath)
    {
        _provider = provider;
        _database = database;
        DatabasePath = databasePath;
        DatabaseName = "munarium-test";
        Store = new SharpCoreDbSourceStore(database);
        Registry = new SharpCoreDbSourceRegistry(database);
        IndexVersions = new SharpCoreDbIndexVersionStore(database);
        Idempotency = new SharpCoreDbIdempotencyStore(database);
        Evidence = new SharpCoreDbEvidenceStore(database);
        Sessions = new SharpCoreDbSessionStore(database);
        Runbooks = new SharpCoreDbRunbookStore(database);
        Audit = new SharpCoreDbAccessTokenAudit(database);
        Chunks = new SharpCoreDbIndexChunkStore(database);
    }

    /// <summary>Gets the index-chunk store under test.</summary>
    public SharpCoreDbIndexChunkStore Chunks { get; }

    /// <summary>Gets the capability audit under test.</summary>
    public SharpCoreDbAccessTokenAudit Audit { get; }

    /// <summary>Gets the runbook store under test.</summary>
    public SharpCoreDbRunbookStore Runbooks { get; }

    /// <summary>Gets the session store under test.</summary>
    public SharpCoreDbSessionStore Sessions { get; }

    /// <summary>Gets the evidence store under test.</summary>
    public SharpCoreDbEvidenceStore Evidence { get; }

    /// <summary>Gets the idempotency store under test.</summary>
    public SharpCoreDbIdempotencyStore Idempotency { get; }

    /// <summary>Gets the index-version store under test.</summary>
    public SharpCoreDbIndexVersionStore IndexVersions { get; }

    /// <summary>Gets the store under test.</summary>
    public SharpCoreDbSourceStore Store { get; }

    /// <summary>Gets the registry under test.</summary>
    public SharpCoreDbSourceRegistry Registry { get; }

    /// <summary>Gets where the database lives, so a test can reopen it.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets the database's name, so a test can reopen it.</summary>
    public string DatabaseName { get; }

    /// <summary>Opens a database, in a temporary location when none is given.</summary>
    /// <param name="databasePath">The database to open, or <see langword="null"/> for a fresh one.</param>
    /// <returns>The fixture, to be disposed.</returns>
    public static SourceStoreFixture Create(string? databasePath = null) =>
        Open(databasePath ?? Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}"));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
    }

    private static SourceStoreFixture Open(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddSharpCoreDB();

        var provider = services.BuildServiceProvider();
        var database = provider
            .GetRequiredService<DatabaseFactory>()
            .Create(databasePath, "munarium-test");

        return new SourceStoreFixture(provider, database, databasePath);
    }
}
