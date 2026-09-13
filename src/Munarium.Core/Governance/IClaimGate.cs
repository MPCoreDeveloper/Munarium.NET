namespace Munarium.Governance;

using Munarium.Commands;

/// <summary>
/// One governance rule on the command path.
/// </summary>
/// <remarks>
/// A gate returns a verdict rather than throwing: a blocked claim is recorded as disputed, so the
/// refusal is a value the write path carries, not an exception that discards the claim.
/// </remarks>
public interface IClaimGate
{
    /// <summary>Gets the gate's name, which is recorded with a disputed claim.</summary>
    string Name { get; }

    /// <summary>
    /// Evaluates a claim.
    /// </summary>
    /// <param name="command">The claim being recorded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="Permitted"/> or <see cref="Blocked"/>.</returns>
    ValueTask<ClaimVerdict> EvaluateAsync(RecordClaimCommand command, CancellationToken cancellationToken = default);
}
