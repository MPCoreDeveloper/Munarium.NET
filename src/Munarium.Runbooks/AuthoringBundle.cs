namespace Munarium.Runbooks;

using System.Text;
using Munarium.Evidence;

/// <summary>
/// The export bundle: a self-contained, hash-manifested document carrying a validated set from where it was authored to
/// wherever it is applied.
/// </summary>
/// <remarks>
/// Deliberately dependency-light, because the point is that it can be checked anywhere: the documents travel verbatim,
/// each carries its own digest, and the manifest digest is taken over the sorted paths and their digests, so drift
/// between an export and an apply is detectable with nothing but a sha256 implementation. No archive and no compression,
/// for the same reason - a bundle a reviewer can read is a bundle a reviewer can disagree with.
/// <para>
/// The order is part of the contract rather than a hint: a collection binds a shape, so a set applied out of order is a
/// set that served nothing for as long as its shape was missing.
/// </para>
/// </remarks>
public sealed record AuthoringBundle
{
    /// <summary>The bundle kind, which a reader recognizes before it reads anything else.</summary>
    public const string Kind = "MunariumAuthoringBundle";

    /// <summary>The name a bundle says wrote it.</summary>
    public const string ToolName = "munarium-server";

    /// <summary>The bundle format's own version, which is not the contract's.</summary>
    /// <remarks>
    /// A bundle outlives the deployment that produced it - it is what an author hands to an operator - so it carries the
    /// version of the format rather than the version of anything that happened to write it.
    /// </remarks>
    public const string ApiVersion = "munarium.ioka.io/v1";

    /// <summary>Gets what wrote it.</summary>
    public required BundleTool Tool { get; init; }

    /// <summary>Gets the draft it came from.</summary>
    public required string DraftId { get; init; }

    /// <summary>Gets the runbook name the set applies under.</summary>
    public required string Name { get; init; }

    /// <summary>Gets when it was written.</summary>
    public required string CreatedAt { get; init; }

    /// <summary>Gets the documents, keyed by the path each travels under.</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }

    /// <summary>Gets the digest of each document, keyed the same way.</summary>
    public required IReadOnlyDictionary<string, string> Hashes { get; init; }

    /// <summary>Gets the paths in the order they have to be applied in: shapes first.</summary>
    public required IReadOnlyList<string> ApplyOrder { get; init; }

    /// <summary>Gets the digest over the manifest, which is what makes drift detectable.</summary>
    public required string ManifestHash { get; init; }

    /// <summary>Gets what validation said about the set when it was exported.</summary>
    public required BundleValidation Validation { get; init; }

    /// <summary>
    /// Digests a manifest: the byte-sorted path, a separator, the digest and a newline, concatenated.
    /// </summary>
    /// <remarks>
    /// Sorted, and over the pairs rather than the documents, so the digest depends on what the bundle says about its
    /// contents and not on the order a map happened to enumerate in.
    /// </remarks>
    /// <param name="hashes">The per-document digests.</param>
    /// <returns>The manifest digest.</returns>
    public static string ManifestDigest(IReadOnlyDictionary<string, string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        var manifest = new StringBuilder();

        foreach (var path in hashes.Keys.OrderBy(path => path, StringComparer.Ordinal))
        {
            manifest.Append(path).Append('\0').Append(hashes[path]).Append('\n');
        }

        return ArtifactContent.Hash(manifest.ToString());
    }

    /// <summary>
    /// Builds a bundle from a materialized set.
    /// </summary>
    /// <param name="draftId">The draft it came from.</param>
    /// <param name="createdAt">When it was written, stamped by the caller because nothing here reads a clock.</param>
    /// <param name="toolVersion">The version of what wrote it.</param>
    /// <param name="set">The materialized set.</param>
    /// <param name="validation">What validation said.</param>
    /// <returns>The bundle.</returns>
    public static AuthoringBundle Build(
        string draftId,
        string createdAt,
        string toolVersion,
        Materialized set,
        BundleValidation validation)
    {
        ArgumentNullException.ThrowIfNull(set);

        var hashes = set.Documents.ToDictionary(
            entry => entry.Key,
            entry => ArtifactContent.Hash(entry.Value),
            StringComparer.Ordinal);

        return new AuthoringBundle
        {
            Tool = new BundleTool { Name = ToolName, Version = toolVersion },
            DraftId = draftId,
            Name = RunbookName(set),
            CreatedAt = createdAt,
            Files = set.Documents,
            Hashes = hashes,
            ApplyOrder = set.ApplyOrder,
            ManifestHash = ManifestDigest(hashes),
            Validation = validation,
        };
    }

    /// <summary>
    /// Checks a bundle against itself.
    /// </summary>
    /// <remarks>
    /// The bundle carries what it needs to be checked, so this is the check a recipient runs before applying anything: a
    /// kind it recognizes, a digest per document that matches the document, a manifest digest that matches the digests,
    /// and an order naming exactly the documents that travelled. The first problem is returned rather than all of them,
    /// because the rest are rarely interesting once the first one is real.
    /// </remarks>
    /// <param name="bundle">The bundle.</param>
    /// <returns>The first problem, or <see langword="null"/> when the bundle agrees with itself.</returns>
    public static string? Verify(AuthoringBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (bundle.Files.Count != bundle.Hashes.Count)
        {
            return "the documents and the digests disagree about which documents are in the bundle";
        }

        foreach (var (path, document) in bundle.Files)
        {
            if (!bundle.Hashes.TryGetValue(path, out var declared))
            {
                return $"'{path}' carries no digest";
            }

            var actual = ArtifactContent.Hash(document);

            if (!string.Equals(declared, actual, StringComparison.Ordinal))
            {
                return $"'{path}' is not what its digest says it is (declared {declared}, actual {actual})";
            }
        }

        var manifest = ManifestDigest(bundle.Hashes);

        if (!string.Equals(manifest, bundle.ManifestHash, StringComparison.Ordinal))
        {
            return "the manifest digest does not match the digests it covers";
        }

        // Sorted, because the order is a claim about applying rather than about listing: what has to hold is that the
        // order names exactly these documents, not that it names them the way a map happened to enumerate.
        var ordered = bundle.ApplyOrder.OrderBy(path => path, StringComparer.Ordinal);
        var files = bundle.Files.Keys.OrderBy(path => path, StringComparer.Ordinal);

        return ordered.SequenceEqual(files, StringComparer.Ordinal)
            ? null
            : "the apply order does not name exactly the documents in the bundle";
    }

    /// <summary>Finds the name a materialized set applies under.</summary>
    /// <param name="set">The materialized set.</param>
    /// <returns>The name, taken from the path the runbook travels under.</returns>
    private static string RunbookName(Materialized set)
    {
        var path = set.ApplyOrder.LastOrDefault(candidate =>
            !candidate.StartsWith(AuthoringMaterializer.ShapeDirectory, StringComparison.Ordinal))
            ?? string.Empty;

        var name = Path.GetFileNameWithoutExtension(path);

        return name.Length == 0 ? path : name;
    }
}

/// <summary>What wrote a bundle.</summary>
public sealed record BundleTool
{
    /// <summary>Gets the tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets its version.</summary>
    public required string Version { get; init; }
}

/// <summary>
/// What validation said about a set, counted.
/// </summary>
/// <remarks>
/// Counted rather than listed: the findings themselves belong to whoever validated, and a bundle carrying them would be a
/// report travelling as a document. The counts are what a recipient needs in order to decide, and validity is what says
/// the decision at all.
/// </remarks>
public sealed record BundleValidation
{
    /// <summary>Gets whether nothing found was an error.</summary>
    public required bool Valid { get; init; }

    /// <summary>Gets how many findings were errors.</summary>
    public required int Errors { get; init; }

    /// <summary>Gets how many were warnings.</summary>
    public required int Warns { get; init; }

    /// <summary>Gets how many were advisory.</summary>
    public required int Infos { get; init; }

    /// <summary>Counts a set of findings.</summary>
    /// <param name="findings">The findings.</param>
    /// <returns>The summary.</returns>
    public static BundleValidation From(IReadOnlyList<ValidationFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return new BundleValidation
        {
            Valid = RunbookValidation.IsValid(findings),
            Errors = findings.Count(finding => finding.Severity == Runbooks.Severity.Error),
            Warns = findings.Count(finding => finding.Severity == Runbooks.Severity.Warn),
            Infos = findings.Count(finding => finding.Severity == Runbooks.Severity.Info),
        };
    }
}