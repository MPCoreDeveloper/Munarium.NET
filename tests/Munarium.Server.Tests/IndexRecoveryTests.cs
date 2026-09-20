namespace Munarium.Server.Tests;

using Microsoft.Extensions.DependencyInjection;
using Munarium.Shapes;
using Munarium.Store.SharpCoreDb;
using Munarium.Wire;
using SharpCoreDB;
using SharpCoreDB.Interfaces;

/// <summary>Tests for what a restart does with a live index version.</summary>
/// <remarks>
/// The end-to-end claim the chunk store exists for: a deployment that comes back reads what was persisted instead of
/// reading, extracting and embedding the corpus again - and a version nothing was persisted for is still built, because
/// a fallback that had been removed would be a deployment that cannot start at all.
/// </remarks>
public class IndexRecoveryTests
{
    private const string DocumentPath = "docs/a.txt";
    private const string Document = "The Bell rang twice.\n\nA second paragraph, longer, with a sentence in it.\n";

    /// <summary>A build leaves its chunks in the database, with the source they came from.</summary>
    [Fact]
    public async Task ABuildLeavesItsChunksInTheDatabase()
    {
        var built = await LiveVersionAsync();

        await using var database = Open(built.Path, built.Name);
        var persisted = await new SharpCoreDbIndexChunkStore(database).ReadAsync(built.VersionId);

        Assert.NotEmpty(persisted);
        Assert.All(persisted, chunk => Assert.Equal(DocumentPath, chunk.Source.SourcePath));
        Assert.All(persisted, chunk => Assert.NotEmpty(chunk.Embedding));
    }

    /// <summary>A version whose chunks were persisted comes back as a load.</summary>
    [Fact]
    public async Task ARestartLoadsAVersionWhoseChunksWerePersisted()
    {
        var built = await LiveVersionAsync();

        await using var reopened = MunariumKernel.Create(built.Path, built.Name, Shapes());
        var recovery = await reopened.RecoverAsync();

        var one = Assert.Single(recovery);

        Assert.True(one.Loaded, one.Refusal ?? "the version was rebuilt although its chunks were persisted");
        Assert.Null(one.Refusal);
        Assert.Equal(built.VersionId, one.IndexVersionId);
    }

    /// <summary>A version with nothing persisted is built again, and lands on the same identity.</summary>
    [Fact]
    public async Task ARestartRebuildsAVersionWithNothingPersisted()
    {
        var built = await LiveVersionAsync(dropChunks: true);

        await using var reopened = MunariumKernel.Create(built.Path, built.Name, Shapes());
        var recovery = await reopened.RecoverAsync();

        var one = Assert.Single(recovery);

        Assert.False(one.Loaded);
        Assert.Null(one.Refusal);

        // The rebuild reads the same corpus through the same manifest, so it is the same version rather than a second
        // one - which is what lets a cutover name a version that a previous process built.
        Assert.Equal(built.VersionId, one.IndexVersionId);
    }

    /// <summary>Builds one live version over one document, and answers where it lives.</summary>
    /// <param name="dropChunks">Whether to forget the version's chunks afterwards, to exercise the fallback.</param>
    /// <returns>Where the database is, what it is called, and the version that was built.</returns>
    private static async Task<(string Path, string Name, string VersionId)> LiveVersionAsync(
        bool dropChunks = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");
        var name = "munarium-recovery";
        string versionId;

        await using (var kernel = MunariumKernel.Create(path, name, Shapes()))
        {
            var ingest = await kernel
                .Operations
                .IngestSourceAsync(new WireSourceIngest(DocumentPath, "text/plain", Document));

            Assert.True(ingest is WireIngestedSource, $"the ingest was refused: {ingest}");

            var built = await kernel
                .Operations
                .BuildIndexVersionAsync(
                    new WireIndexBuild("col-docs", "Documents", "vendor@1", "docs/", Activate: true));

            versionId = built is WireIndexVersion version
                ? version.IndexVersionId
                : throw new InvalidOperationException($"the build was refused: {built}");
        }

        if (dropChunks)
        {
            await using var database = Open(path, name);

            Assert.True(await new SharpCoreDbIndexChunkStore(database).DropAsync(versionId));
        }

        return (path, name, versionId);
    }

    /// <summary>Opens the same database a kernel opened.</summary>
    /// <param name="path">Where it lives.</param>
    /// <param name="name">What it is called.</param>
    /// <returns>The database, to be disposed.</returns>
    private static IDatabase Open(string path, string name) =>
        new ServiceCollection()
            .AddSharpCoreDB()
            .BuildServiceProvider()
            .GetRequiredService<DatabaseFactory>()
            .Create(path, name);

    /// <summary>The registry a build needs, which is one shape with a permissive body.</summary>
    /// <returns>The registry.</returns>
    private static ShapeRegistry Shapes() => new(
    [
        new FactShape
        {
            Name = "vendor",
            Version = 1,
            Identity = ["subject", "key"],
            Schema = """{"type":"object"}""",
        },
    ]);
}
