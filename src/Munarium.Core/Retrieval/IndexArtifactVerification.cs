namespace Munarium.Retrieval;

/// <summary>One thing wrong with a persisted index artifact.</summary>
/// <param name="Code">The stable dotted code, which is what a caller switches on.</param>
/// <param name="Message">What is wrong, in the terms an operator can act on.</param>
public sealed record IndexArtifactFinding(string Code, string Message);

/// <summary>What verifying a persisted artifact found.</summary>
/// <param name="Verified">Whether every check passed.</param>
/// <param name="Findings">Everything that did not, in a deterministic order.</param>
public sealed record ArtifactVerification(bool Verified, IReadOnlyList<IndexArtifactFinding> Findings);

/// <summary>Checks a version's persisted chunks against the manifest that names them.</summary>
/// <remarks>
/// The checks run over the chunks themselves and never over a projection of them: a version id, a count or a manifest
/// hash can all be recorded correctly while the bytes they stand for are missing, truncated or from another build. So
/// every check here reads what was persisted and compares it against what the manifest claims - the same stance as the
/// original's, whose verify opens the artifact from its store because reading the catalogue's own projection "would
/// verify the database against itself".
/// <para>
/// What a single-node deployment can check is what belongs in one artifact: that the vectors have the width the manifest
/// records, that the chunk text obeys the maximum the build cut to, that every source the manifest names is represented
/// and nothing else is, and that the ordinals of a document are contiguous - because a gap is the one failure a count
/// cannot see. It cannot check a fleet, and it does not pretend to: the slots, generations and promotion gate the
/// original has belong to a mirrored deployment, and they are a capability of their own rather than a stricter form of
/// this one.
/// </para>
/// </remarks>
public static class IndexArtifactVerification
{
    /// <summary>Verifies a version's persisted chunks.</summary>
    /// <param name="version">The version as the catalogue records it, whose manifest is the claim.</param>
    /// <param name="chunks">The chunks a store read back for it.</param>
    /// <returns>What the checks found.</returns>
    public static ArtifactVerification Verify(IndexVersion version, IReadOnlyList<PersistedChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(chunks);

        var findings = new List<IndexArtifactFinding>();

        if (chunks.Count == 0)
        {
            findings.Add(new IndexArtifactFinding(
                "artifact.empty",
                $"no chunk is persisted for '{version.Id}', so there are no bytes to verify"));

            return new ArtifactVerification(false, findings);
        }

        var manifest = version.Manifest;
        var expected = manifest.SourceContentHashes.ToHashSet(StringComparer.Ordinal);
        var represented = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chunk in chunks)
        {
            var hash = chunk.Source.ContentHash;

            if (!expected.Contains(hash))
            {
                findings.Add(new IndexArtifactFinding(
                    "artifact.source-unknown",
                    $"'{chunk.Source.ChunkId}' cites '{hash}', which the manifest does not name"));
            }

            represented.Add(hash);

            if (chunk.Embedding.Count != manifest.Embedder.Dimensions)
            {
                findings.Add(new IndexArtifactFinding(
                    "artifact.embedding-dimensions",
                    $"'{chunk.Source.ChunkId}' carries {chunk.Embedding.Count} dimensions and the manifest records "
                    + $"{manifest.Embedder.Dimensions}"));
            }

            if (chunk.Text.Length > manifest.MaxChars)
            {
                findings.Add(new IndexArtifactFinding(
                    "artifact.chunk-too-long",
                    $"'{chunk.Source.ChunkId}' is {chunk.Text.Length} characters and the build cut to "
                    + $"{manifest.MaxChars}"));
            }
        }

        foreach (var missing in expected.Except(represented, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new IndexArtifactFinding(
                "artifact.source-missing",
                $"'{missing}' is named by the manifest and no persisted chunk cites it"));
        }

        foreach (var source in chunks.GroupBy(chunk => chunk.Source.SourceId, StringComparer.Ordinal))
        {
            var ordinals = source.Select(chunk => chunk.Source.ChunkOrdinal).Order().ToArray();

            for (var position = 0; position < ordinals.Length; position++)
            {
                if (ordinals[position] != position)
                {
                    findings.Add(new IndexArtifactFinding(
                        "artifact.ordinal-gap",
                        $"'{source.Key}' has {ordinals.Length} persisted chunks whose ordinals start at "
                        + $"{ordinals[0]}, so a chunk of it is missing"));

                    break;
                }
            }
        }

        return new ArtifactVerification(findings.Count == 0, findings);
    }
}
