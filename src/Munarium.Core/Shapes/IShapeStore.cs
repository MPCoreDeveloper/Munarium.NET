namespace Munarium.Shapes;

/// <summary>
/// Where published shapes are kept.
/// </summary>
/// <remarks>
/// A shape an operator applied has to survive a restart, or a deployment would come back serving runbooks that bind a
/// document it no longer has. It is a seam of its own rather than part of the registry: the registry is what a running
/// deployment resolves against, and this is what the next start reads.
/// </remarks>
public interface IShapeStore
{
    /// <summary>
    /// Publishes a shape, replacing whatever was stored under its name.
    /// </summary>
    /// <remarks>
    /// A replacement rather than an addition, because a name is the identity a runbook binds and two stored documents
    /// under one name would make which one a deployment serves a matter of file order.
    /// </remarks>
    /// <param name="shape">The shape.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the shape is durable.</returns>
    /// <exception cref="ArgumentException">Thrown when the shape's name cannot be stored under.</exception>
    ValueTask PublishAsync(FactShape shape, CancellationToken cancellationToken = default);
}