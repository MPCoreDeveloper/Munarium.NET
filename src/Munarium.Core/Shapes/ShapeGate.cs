namespace Munarium.Shapes;

using Munarium.Commands;
using Munarium.Governance;

/// <summary>
/// The gate that enforces a shape: a claim's body has to satisfy the shape's schema.
/// </summary>
/// <remarks>
/// A schema violation is not an error and not a drop - it is a verdict, and the claim is recorded as
/// disputed with the reason attached. That is the point of putting shapes on the command path: the
/// refusal becomes evidence rather than a lost write.
/// </remarks>
/// <param name="shapes">The registry the claim's shape is resolved from.</param>
public sealed class ShapeGate(ShapeRegistry shapes) : IClaimGate
{
    private readonly ShapeRegistry _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));

    /// <inheritdoc />
    public string Name => "shape";

    /// <inheritdoc />
    public ValueTask<ClaimVerdict> EvaluateAsync(
        RecordClaimCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_shapes.TryResolve(command.Shape, out var shape))
        {
            return Refuse($"no shape named '{command.Shape}' is registered");
        }

        var validation = FactSchemaValidator.Validate(shape.Schema, command.Body);

        return validation.Valid
            ? ValueTask.FromResult<ClaimVerdict>(Permitted.Instance)
            : Refuse(string.Join(" ", validation.Errors));
    }

    private ValueTask<ClaimVerdict> Refuse(string reason) =>
        ValueTask.FromResult<ClaimVerdict>(new Blocked(Name, reason));
}
