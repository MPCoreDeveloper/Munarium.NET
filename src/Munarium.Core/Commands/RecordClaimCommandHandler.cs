namespace Munarium.Commands;

using Munarium.Governance;
using Munarium.Ledger;
using SharpDispatch;

/// <summary>
/// The command path for recording a claim: judge, then write.
/// </summary>
/// <remarks>
/// A disputed claim is an <c>Ok</c>: the command's job was to put the claim and its governance
/// verdict into the ledger, and it did. Only a contended write - one that lost every retry -
/// fails the command.
/// </remarks>
/// <param name="ledger">The kernel's claim write path.</param>
public sealed class RecordClaimCommandHandler(ClaimLedger ledger) : ICommandHandler<RecordClaimCommand>
{
    private readonly ClaimLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <inheritdoc />
    public async Task<CommandDispatchResult> HandleAsync(
        RecordClaimCommand command,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _ledger.RecordAsync(command, cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            ClaimAsserted asserted => CommandDispatchResult.Ok($"asserted at {asserted.Head}"),
            ClaimRecordedAsDisputed disputed => CommandDispatchResult.Ok($"disputed by {disputed.Gate} at {disputed.Head}"),
            ClaimContended contended => CommandDispatchResult.Fail($"write contended {contended.Expected}->{contended.Actual}"),
        };
    }
}
