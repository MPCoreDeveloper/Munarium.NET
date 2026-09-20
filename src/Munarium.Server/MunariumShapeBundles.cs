namespace Munarium.Server;

using System.Text.Json;
using Munarium.Shapes;

/// <summary>
/// Loads shapes from a directory of JSON files: one shape per file.
/// </summary>
/// <remarks>
/// Shapes are declarative and versioned, so they belong in files a deployment can read and a
/// reviewer can diff, not in code. A missing or unconfigured directory yields an empty registry,
/// which is a valid deployment: it can still read the ledger, it just cannot write a claim.
/// </remarks>
public static class MunariumShapeBundles
{
    /// <summary>
    /// Loads every shape under a directory.
    /// </summary>
    /// <param name="directory">The directory to read, or <see langword="null"/> for none.</param>
    /// <returns>The registry.</returns>
    public static ShapeRegistry Load(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new ShapeRegistry([]);
        }

        var shapes = new List<FactShape>();

        // Ordered, so which shape is registered first does not depend on the file system.
        foreach (var file in Directory
            .EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            shapes.Add(Read(File.ReadAllText(file)));
        }

        return new ShapeRegistry(shapes);
    }

    /// <summary>
    /// Opens the store that publishes shapes under a directory.
    /// </summary>
    /// <remarks>
    /// The same directory the loader reads, so a shape an operator applied is served again after a restart. A published
    /// shape a restart forgot would silently take the runbooks that bind it with it, which is worse than refusing the
    /// publish in the first place.
    /// </remarks>
    /// <param name="directory">The directory, or <see langword="null"/> when this deployment serves none.</param>
    /// <returns>The store, or <see langword="null"/> when there is nowhere to publish.</returns>
    public static IShapeStore? Store(string? directory) =>
        string.IsNullOrWhiteSpace(directory) ? null : new BundleDirectoryShapeStore(directory);

    /// <summary>Reads one shape from its on-disk form.</summary>
    /// <param name="json">The shape as JSON.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="JsonException">Thrown when the document is not a shape.</exception>
    public static FactShape Read(string json) => ShapeDocuments.Read(json);
}

/// <summary>Publishes shapes as files, one shape per file, in the directory the loader reads.</summary>
/// <remarks>
/// The name is the file name, which is what makes a published shape findable by the name a runbook binds: a shape name
/// that cannot be a file name is refused rather than stored under a name of this deployment\'s own choosing, because a
/// document served under one name and stored under another is a document an operator cannot account for.
/// </remarks>
internal sealed class BundleDirectoryShapeStore : IShapeStore
{
    private readonly string _directory;

    /// <summary>Initializes a new instance of the <see cref="BundleDirectoryShapeStore"/> class.</summary>
    /// <param name="directory">The directory to write into.</param>
    internal BundleDirectoryShapeStore(string directory)
    {
        _directory = directory;
        _ = Directory.CreateDirectory(directory);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">Thrown when the name cannot be a file name.</exception>
    public async ValueTask PublishAsync(FactShape shape, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shape);

        if (shape.Name.Length == 0 || shape.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"The shape name '{shape.Name}' cannot be a file name, so this deployment will not store it.",
                nameof(shape));
        }

        await File
            .WriteAllTextAsync(
                Path.Combine(_directory, string.Concat(shape.Name, ".json")),
                ShapeDocuments.Write(shape),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
