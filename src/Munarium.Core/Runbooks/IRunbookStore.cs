namespace Munarium.Runbooks;

/// <summary>
/// Persistence for applied runbooks.
/// </summary>
/// <remarks>
/// A seam of its own rather than part of the ledger's: a runbook is configuration an operator applied, not a claim
/// somebody asserted about the world, and nothing about a deployment's history depends on which document version was
/// live on a given day except the turns that ran - and those record the pin themselves.
/// <para>
/// The kernel owns the seam and the rules a record has to satisfy; the implementations are the store's.
/// </para>
/// </remarks>
public interface IRunbookStore
{
    /// <summary>
    /// Applies a version.
    /// </summary>
    /// <remarks>
    /// An upsert keyed by the reference, and re-applying resets an in-flight removal: the bytes changed, so a removal
    /// armed against the previous content must not be able to remove the fresh version. That is why a removal carries an
    /// identity rather than being a state a document can inherit.
    /// </remarks>
    /// <param name="record">The version to apply; its timestamps are stamped by the store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record as stored.</returns>
    ValueTask<RunbookRecord> ApplyAsync(RunbookRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one version by its exact reference.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="runbookRef">The reference, <c>name@version</c>.</param>
    /// <param name="includeRemoved">Whether a removed version may be read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record, or <see langword="null"/> when there is none this caller may see.</returns>
    ValueTask<RunbookRecord?> GetAsync(
        string tenant,
        string runbookRef,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves whatever a caller named: a reference or a bare name.
    /// </summary>
    /// <remarks>
    /// A reference resolves to exactly that version, because a caller that named a version has already decided which
    /// document it wants. A bare name resolves to the newest usable version, in numeric order rather than lexical: with
    /// versions in the double digits, a string comparison would answer <c>9</c> for <c>10</c>.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="nameOrRef">The name, or the reference.</param>
    /// <param name="includeRemoved">Whether a removed version may be resolved.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record, or <see langword="null"/> when nothing matches.</returns>
    ValueTask<RunbookRecord?> ResolveAsync(
        string tenant,
        string nameOrRef,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the applied versions.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="includeRemoved">Whether removed versions are listed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The records, ordered by name and then by version, oldest first.</returns>
    ValueTask<IReadOnlyList<RunbookRecord>> ListAsync(
        string tenant,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Arms a removal.
    /// </summary>
    /// <remarks>
    /// Removal takes two passes because it is destructive in a way that is not reversible: the first pass records who
    /// asked and what they were asking about, and the version stays usable until somebody confirms. The identity is
    /// minted by the caller and echoed back on confirmation, so a confirmation cannot remove a version it did not arm.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="runbookRef">The version to remove.</param>
    /// <param name="removalId">The removal's identity, which the confirmation has to repeat.</param>
    /// <param name="at">When it was asked for.</param>
    /// <param name="by">Who asked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The armed record, or <see langword="null"/> when the version is unknown or already removed.</returns>
    ValueTask<RunbookRecord?> RequestRemovalAsync(
        string tenant,
        string runbookRef,
        string removalId,
        string at,
        string? by,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms an armed removal.
    /// </summary>
    /// <remarks>
    /// Only the removal that armed the row may confirm it, and only while it is still armed: a version that was
    /// re-applied in the meantime is a different document, and its status is active again.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="runbookRef">The version to remove.</param>
    /// <param name="removalId">The removal's identity, which has to match the armed one.</param>
    /// <param name="at">When it was confirmed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The removed record, or <see langword="null"/> when nothing was armed under that identity.</returns>
    ValueTask<RunbookRecord?> ConfirmRemovalAsync(
        string tenant,
        string runbookRef,
        string removalId,
        string at,
        CancellationToken cancellationToken = default);
}
