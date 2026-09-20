namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Retrieval;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>Tests for <see cref="SharpCoreDbIndexChunkStore"/>: a version's chunks, written once and read back.</summary>
/// <remarks>
/// The whole point of the store is that its rows outlive the process that wrote them, so the strongest test here writes,
/// disposes, opens a second fixture over the same database and reads.
/// </remarks>
public class SharpCoreDbIndexChunkStoreTests
{
    /// <summary>Chunks come back as they went in, ordered by their position in the source.</summary>
    [Fact]
    public async Task ChunksAreReadBackInSourceOrder()
    {
        await using var fixture = SourceStoreFixture.Create();

        var written = await fixture.Chunks.WriteAsync("idx-one", [Chunk(1, "second"), Chunk(0, "first")]);
        var read = await fixture.Chunks.ReadAsync("idx-one");

        Assert.Equal(2, written);
        Assert.Equal(["first", "second"], read.Select(chunk => chunk.Text));
        Assert.Equal([0, 1], read.Select(chunk => chunk.Source.ChunkOrdinal));
        Assert.Equal("sha256:doc", read[0].Source.ContentHash);
        Assert.Equal("docs/note.txt", read[0].Source.SourcePath);
        Assert.Equal([0.25f, 0.5f], read[0].Embedding);
    }

    /// <summary>A version with no chunks reads as empty, and dropping it says whether there was anything.</summary>
    [Fact]
    public async Task AnAbsentVersionReadsEmptyAndDropsFalse()
    {
        await using var fixture = SourceStoreFixture.Create();

        Assert.Empty(await fixture.Chunks.ReadAsync("idx-nothing"));
        Assert.False(await fixture.Chunks.DropAsync("idx-nothing"));

        await fixture.Chunks.WriteAsync("idx-one", [Chunk(0, "first")]);

        Assert.True(await fixture.Chunks.DropAsync("idx-one"));
        Assert.Empty(await fixture.Chunks.ReadAsync("idx-one"));
    }

    /// <summary>One version's chunks are its own table, so another version does not see them.</summary>
    [Fact]
    public async Task VersionsDoNotSeeEachOther()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Chunks.WriteAsync("idx-one", [Chunk(0, "one")]);
        await fixture.Chunks.WriteAsync("idx-two", [Chunk(0, "two")]);

        Assert.Equal(["one"], (await fixture.Chunks.ReadAsync("idx-one")).Select(chunk => chunk.Text));
        Assert.Equal(["two"], (await fixture.Chunks.ReadAsync("idx-two")).Select(chunk => chunk.Text));
    }

    /// <summary>A version's chunks outlive the process that wrote them.</summary>
    [Fact]
    public async Task ChunksOutliveTheProcessThatWroteThem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");

        await using (var first = SourceStoreFixture.Create(path))
        {
            await first.Chunks.WriteAsync("idx-one", [Chunk(0, "durable")]);
        }

        await using var reopened = SourceStoreFixture.Create(path);
        var read = await reopened.Chunks.ReadAsync("idx-one");

        Assert.Equal(["durable"], read.Select(chunk => chunk.Text));
        Assert.Equal([0.25f, 0.5f], read[0].Embedding);
    }

    /// <summary>The chunk a test writes.</summary>
    /// <param name="ordinal">Its position in the source.</param>
    /// <param name="text">Its text.</param>
    /// <returns>The chunk.</returns>
    private static PersistedChunk Chunk(int ordinal, string text) => new()
    {
        Source = new SourceReference($"chunk-{ordinal}", "source-1", "docs/note.txt", "sha256:doc", ordinal),
        Text = text,
        Embedding = [0.25f, 0.5f],
    };
}
