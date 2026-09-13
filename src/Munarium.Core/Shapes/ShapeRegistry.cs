namespace Munarium.Shapes;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// The shape registry: the shapes a deployment understands, resolved by name.
/// </summary>
/// <remarks>
/// Registration is the whole mutable surface, and it happens once at composition. Resolution is then
/// a lookup, so the write path never pays for a scan and a shape can never change under a running
/// ledger.
/// </remarks>
public sealed class ShapeRegistry
{
    private readonly Dictionary<string, FactShape> _shapes = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ShapeRegistry"/> class.
    /// </summary>
    /// <param name="shapes">The shapes to register.</param>
    /// <exception cref="ArgumentException">Thrown when a shape name is registered twice.</exception>
    public ShapeRegistry(IEnumerable<FactShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);

        foreach (var shape in shapes)
        {
            ArgumentNullException.ThrowIfNull(shape);

            if (!_shapes.TryAdd(shape.Name, shape))
            {
                throw new ArgumentException($"The shape '{shape.Name}' is registered twice.", nameof(shapes));
            }
        }
    }

    /// <summary>Gets the number of registered shapes.</summary>
    public int Count => _shapes.Count;

    /// <summary>
    /// Resolves a shape by name.
    /// </summary>
    /// <param name="name">The shape name.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when no shape has that name.</exception>
    public FactShape Resolve(string name) =>
        TryResolve(name, out var shape)
            ? shape
            : throw new KeyNotFoundException($"No shape named '{name}' is registered.");

    /// <summary>
    /// Derives a claim's lineage from its shape and body.
    /// </summary>
    /// <param name="name">The shape name.</param>
    /// <param name="bodyJson">The fact body.</param>
    /// <returns>
    /// The lineage. This is total on purpose: a shape this deployment does not know still yields a
    /// stable lineage, so a claim naming an unknown shape is recorded as disputed instead of failing
    /// the write - a refusal that cannot be recorded is not governance.
    /// </returns>
    public string LineageOf(string name, string? bodyJson) =>
        TryResolve(name, out var shape) ? shape.LineageOf(bodyJson) : $"{name}@unregistered";

    /// <summary>
    /// Tries to resolve a shape by name.
    /// </summary>
    /// <param name="name">The shape name.</param>
    /// <param name="shape">The shape, when found.</param>
    /// <returns><see langword="true"/> when the shape is registered.</returns>
    public bool TryResolve(string name, [NotNullWhen(true)] out FactShape? shape) =>
        _shapes.TryGetValue(name, out shape);
}
