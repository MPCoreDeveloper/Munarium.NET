namespace Munarium.Server.Tests;

using Munarium.Wire;

/// <summary>
/// What a deployment does when it comes back: the chunks of an index live in the process that built them, so a restarted
/// deployment has the rows and the version records and rebuilds from those.
/// </summary>
public class KernelRecoveryTests
{
    [Fact]
    public async Task ARestartedDeploymentRebuildsItsLiveVersionFromTheRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");
        var shapes = MunariumShapeBundles.Load(MunariumApiFactory.ShapesDirectory);
        string versionId;

        await using (var first = MunariumKernel.Create(path, "munarium-recovery", shapes))
        {
            var ingested = Ingested(await first.Operations.IngestSourceAsync(
                new WireSourceIngest(
                    "recover/bell.txt", "text/plain", "The Bell rang twice at the north gate.", string.Empty)));

            Assert.True(ingested.ChunksIndexed > 0);

            var built = Built(await first.Operations.BuildIndexVersionAsync(
                new WireIndexBuild("col-recover", "Recovery", "vendor@1", "recover/", Activate: true)));

            versionId = built.IndexVersionId;

            var before = await first.Operations.SearchAsync(new WireSearchQuery("the bell at the north gate", 5));

            Assert.Equal(versionId, before.Envelope.IndexVersion);
        }

        // The second deployment: the same database, no chunks in memory, and the rows to rebuild them from.
        await using var second = MunariumKernel.Create(path, "munarium-recovery", shapes);

        var recovery = Assert.Single(await second.RecoverAsync());

        Assert.Null(recovery.Refusal);
        Assert.Equal("col-recover", recovery.CollectionId);

        // The corpus did not change, so the rebuild mints the same identity: it is the same version, rebuilt.
        Assert.Equal(versionId, recovery.IndexVersionId);

        var after = await second.Operations.SearchAsync(new WireSearchQuery("the bell at the north gate", 5));

        Assert.Equal(versionId, after.Envelope.IndexVersion);
        Assert.Contains(after.Chunks, chunk => chunk.Source.SourcePath == "recover/bell.txt");
        Assert.Contains(after.Envelope.Sources, source => source.SourcePath == "recover/bell.txt");

        // And the answer's provenance still resolves against the version that was rebuilt.
        var resolved = Resolved(await second.Operations.ResolveEnvelopeAsync(
            new WireEnvelopeQuery(after.Envelope.IndexVersion, after.Envelope.LedgerWatermark, after.Envelope.Sources)));

        Assert.True(resolved.Resolved);
    }

    /// <summary>
    /// A build reads the rows a deployment already holds rather than a window of freshly ingested documents, so a prefix
    /// somebody ingested earlier is a rebuild: the second deployment is never handed a document, and serves the
    /// collection anyway.
    /// </summary>
    [Fact]
    public async Task ABuildIsARebuildOfTheRowsSomebodyAlreadyHas()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");
        var shapes = MunariumShapeBundles.Load(MunariumApiFactory.ShapesDirectory);

        await using (var first = MunariumKernel.Create(path, "munarium-rebuild", shapes))
        {
            var ingested = Ingested(await first.Operations.IngestSourceAsync(
                new WireSourceIngest(
                    "rebuild/bell.txt", "text/plain", "The Bell rang twice at the north gate.", string.Empty)));

            Assert.True(ingested.ChunksIndexed > 0);
        }

        await using var second = MunariumKernel.Create(path, "munarium-rebuild", shapes);

        // No ingest runs here: the build reads the prefix's rows out of the registry and their bytes out of the store.
        var built = Built(await second.Operations.BuildIndexVersionAsync(
            new WireIndexBuild("col-rebuild", "Rebuild", "vendor@1", "rebuild/", Activate: true)));

        var found = await second.Operations.SearchAsync(new WireSearchQuery("the bell at the north gate", 5));

        Assert.Equal(built.IndexVersionId, found.Envelope.IndexVersion);
        Assert.Contains(found.Chunks, chunk => chunk.Source.SourcePath == "rebuild/bell.txt");
    }

    [Fact]
    public async Task ADeploymentWithNothingBuiltHasNothingToRecover()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");
        var shapes = MunariumShapeBundles.Load(MunariumApiFactory.ShapesDirectory);

        await using var kernel = MunariumKernel.Create(path, "munarium-fresh", shapes);

        Assert.Empty(await kernel.RecoverAsync());
    }

    private static WireIngestedSource Ingested(WireIngestResult outcome) =>
        outcome is WireIngestedSource ingested ? ingested : throw new InvalidOperationException("Nothing was ingested.");

    private static WireIndexVersion Built(WireIndexBuildResult outcome) =>
        outcome is WireIndexVersion version ? version : throw new InvalidOperationException("Nothing was built.");

    private static WireEnvelopeResolution Resolved(WireEnvelopeResult outcome) =>
        outcome is WireEnvelopeResolution resolution
            ? resolution
            : throw new InvalidOperationException("Nothing was resolved.");
}
