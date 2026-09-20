namespace Munarium.Shapes;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// The shape registry: the shapes a deployment understands, resolved by name.
/// </summary>
/// <remarks>
/// Registration happens at composition, and publishing is the one thing that can change afterwards - because a
/// deployment serves the shapes an operator applied, not only the ones it shipped with.
/// <para>
/// Publishing a registered name replaces it for whatever is written next, and reinterprets nothing already recorded: a
/// claim carries the lineage it was written under, computed once at write time, so the shape that produced an answer can
/// be read back after the shape it came from is gone. That is what makes a mutable registry safe here rather than
/// merely convenient.
/// </para>
/// </remarks>
public sealed class ShapeRegistry
{
    private readonly Dictionary<string, FactShape> _shapes = [];
    private readonly Lock _publish = new();
    private IReadOnlyList<FactShape> _ordered;

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

        // Ordered once, here: a list of shapes has to be deterministic to be usable as an answer.
        _ordered = [.. _shapes.Values.OrderBy(shape => shape.Name, StringComparer.Ordinal)];
    }

    /// <summary>Gets the number of registered shapes.</summary>
    public int Count => _shapes.Count;

    /// <summary>Gets the registered shapes, ordered by name.</summary>
    public IReadOnlyList<FactShape> Shapes => _ordered;

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
    /// Publishes a shape, replacing any shape of the same name.
    /// </summary>
    /// <remarks>
    /// Replacement rather than refusal, because a name is what a runbook binds: refusing to publish <c>documents@2</c>
    /// because <c>documents@1</c> is registered would leave a deployment unable to serve the document its operator just
    /// applied. What it cannot do is change an answer already given, because a claim records the lineage it was written
    /// under rather than deriving it again later.
    /// </remarks>
    /// <param name="shape">The shape.</param>
    /// <returns>Whether that name was registered already, and is therefore replaced.</returns>
    public bool Publish(FactShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        lock (_publish)
        {
            var existed = _shapes.Remove(shape.Name);
            _shapes[shape.Name] = shape;

            // One reference assignment, so a reader sees the set before or the set after and never a half-published one:
            // a caller listing shapes gets a consistent answer without taking a lock to read one.
            _ordered = [.. _shapes.Values.OrderBy(shape => shape.Name, StringComparer.Ordinal)];

            return existed;
        }
    }

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
