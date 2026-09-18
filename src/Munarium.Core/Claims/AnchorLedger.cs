namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// The write path for anchors: a detail is locked, and the lock is recorded as an event of its own.
/// </summary>
/// <remarks>
/// A lock is not a claim. It does not assert a value, it forbids one from changing, which is why it has its own
/// event type and its own write path: the anchor gate reads the locked set out of the snapshot, and a claim that
/// contradicts a lock is reported as an anchor finding rather than as a ledger conflict. The finding has to name
/// the right reason.
/// <para>
/// Upstream stamps an anchor with the position its companion claim will get, because there the two are written as
/// one unit. Here an anchor is an event of its own with its own position, which is what the fold reads - and it is
/// also the honest model of what a lock is: a claim written before the lock was not judged against it, and a claim
/// written after it is.
/// </para>
/// <para>
/// Locking a detail that is already locked replaces the earlier lock, because the plane is keyed by detail and the
/// later event wins. A re-lock is therefore a deliberate restatement rather than a conflict - while a <em>claim</em>
/// that contradicts a lock is refused outright. The asymmetry is the point: a lock is an operator's decision and a
/// claim is an assertion.
/// </para>
/// </remarks>
/// <param name="storage">The ledger's storage seam.</param>
/// <param name="snapshots">Where the locked set is read from, which is what lets a release answer honestly.</param>
/// <param name="maxAttempts">How many times a contended write is retried.</param>
public sealed class AnchorLedger(IStorageBackend storage, MeshSnapshotBuilder snapshots, int maxAttempts = 3)
{
    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly MeshSnapshotBuilder _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly int _maxAttempts = maxAttempts > 0
        ? maxAttempts
        : throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be positive.");

    /// <summary>
    /// Locks a detail.
    /// </summary>
    /// <param name="versionId">The version the lock is taken in.</param>
    /// <param name="detailKey">The detail to lock, as <c>subject.key</c>.</param>
    /// <param name="lockedValue">The value the detail is pinned to.</param>
    /// <param name="lockedAtScope">The scope the lock is taken at, or <see langword="null"/>.</param>
    /// <param name="evidenceJson">The evidence the lock is taken on, as JSON text, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lock as recorded, or the race that stopped it.</returns>
    /// <exception cref="ArgumentException">Thrown when the detail key does not name a property.</exception>
    public async ValueTask<AnchorOutcome> LockAsync(
        string versionId,
        string detailKey,
        string lockedValue,
        string? lockedAtScope = null,
        string? evidenceJson = null,
        CancellationToken cancellationToken = default)
    {
        RequireDetailKey(detailKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockedValue);

        return await AppendAsync(
            versionId,
            AnchorStatus.Locked,
            detailKey,
            lockedValue,
            lockedAtScope,
            evidenceJson,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases a lock, if there is one.
    /// </summary>
    /// <param name="versionId">The version the release is recorded in.</param>
    /// <param name="detailKey">The detail to release, as <c>subject.key</c>.</param>
    /// <param name="releasedAtScope">The scope the release is taken at, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The release as recorded, or the fact that nothing was locked.</returns>
    /// <remarks>
    /// The locked set is read first, because a release of a detail nobody locked is not a write: it would change
    /// nothing and still look like a successful command. The release event itself carries no value - the fold drops
    /// a released anchor by its key, so there is no value left for it to mean.
    /// </remarks>
    public async ValueTask<AnchorReleaseOutcome> ReleaseAsync(
        string versionId,
        string detailKey,
        string? releasedAtScope = null,
        CancellationToken cancellationToken = default)
    {
        RequireDetailKey(detailKey);

        var snapshot = await _snapshots
            .BuildAsync(versionId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        AnchorReleaseOutcome outcome = snapshot.Anchors.ContainsKey(detailKey)
            ? await AppendReleaseAsync(versionId, detailKey, releasedAtScope, cancellationToken).ConfigureAwait(false)
            : new AnchorNotLocked(detailKey);

        return outcome;
    }

    private async ValueTask<AnchorReleaseOutcome> AppendReleaseAsync(
        string versionId,
        string detailKey,
        string? releasedAtScope,
        CancellationToken cancellationToken) =>
        await AppendAsync(
            versionId,
            AnchorStatus.Released,
            detailKey,
            string.Empty,
            releasedAtScope,
            evidenceJson: null,
            cancellationToken).ConfigureAwait(false) switch
        {
            Anchor anchor => anchor,
            WriteContended contended => contended,
        };

    // The event's position is the head it is appended at, re-read on every attempt: a lock that lost the race is
    // retried rather than mis-stamped, because a position a write did not settle at is a position no pin can see.
    private async ValueTask<AnchorOutcome> AppendAsync(
        string versionId,
        AnchorStatus status,
        string detailKey,
        string lockedValue,
        string? scope,
        string? evidenceJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var stream = StreamId.From(versionId);
        var lastExpected = SequenceNumber.Zero;
        var lastActual = SequenceNumber.Zero;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var head = await _storage.HeadAsync(stream, cancellationToken).ConfigureAwait(false);
            lastExpected = head;

            var anchor = new Anchor
            {
                // The ledger hands out the identity, and it is a ULID: the lock carries the instant it was taken
                // at without a field for it.
                Id = LedgerIds.New(),
                VersionId = versionId,
                DetailKey = detailKey,
                LockedValue = lockedValue,
                LockedAtScope = scope,
                Status = status,
                Sequence = new SequenceNumber(head.Value + 1),
                EvidenceJson = evidenceJson,
            };

            var eventType = status is AnchorStatus.Locked
                ? AnchorCodec.LockedEventType
                : AnchorCodec.ReleasedEventType;

            var result = await _storage
                .AppendAsync(stream, head, [new LedgerEvent(eventType, AnchorCodec.Encode(anchor))], cancellationToken)
                .ConfigureAwait(false);

            if (result is Appended appended)
            {
                return anchor with { Sequence = appended.Head };
            }

            if (result is VersionConflict conflict)
            {
                lastExpected = conflict.Expected;
                lastActual = conflict.Actual;
            }
        }

        return new WriteContended(lastExpected, lastActual);
    }

    // A detail key that names no property cannot be matched by a claim, so a lock on it could never be enforced -
    // and an unenforceable lock is worse than no lock, because it reads as protection.
    private static void RequireDetailKey(string detailKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detailKey);

        if (!detailKey.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"a detail key is 'subject.key', and '{detailKey}' names no property to lock.",
                nameof(detailKey));
        }
    }
}

