namespace Munarium.Promises;

using Munarium.Claims;
using Munarium.Ledger;

/// <summary>
/// The write path for promises: one is registered in a scope, and later fulfilled.
/// </summary>
/// <remarks>
/// A promise is the mesh's own commitment - one scope owes another something - so it is recorded rather than
/// judged: no gate refuses a promise, while the promise gate reports one that is still open when its due scope is
/// reached. That is why this write path is short, and why the interesting part of a promise is the read
/// (<see cref="PromiseRegistry.FindOverdue"/>).
/// <para>
/// The plane is keyed by the promise's coordination key and the later event for a key wins, so registering a key
/// that is already registered restates it rather than adding a second obligation. A caller that wants two
/// obligations alive at once gives them two keys - which is also what makes "fulfil this key" unambiguous.
/// </para>
/// </remarks>
/// <param name="storage">The ledger's storage seam.</param>
/// <param name="snapshots">Where the promise plane is read from, so a fulfilment finds what is actually open.</param>
/// <param name="maxAttempts">How many times a contended write is retried.</param>
public sealed class PromiseLedger(IStorageBackend storage, MeshSnapshotBuilder snapshots, int maxAttempts = 3)
{
    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly MeshSnapshotBuilder _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly int _maxAttempts = maxAttempts > 0
        ? maxAttempts
        : throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be positive.");

    /// <summary>
    /// Registers a promise.
    /// </summary>
    /// <param name="versionId">The version it is made in.</param>
    /// <param name="key">Its coordination key.</param>
    /// <param name="kind">What kind of promise it is.</param>
    /// <param name="description">The promise in words.</param>
    /// <param name="originScope">The scope it is made in, or <see langword="null"/>.</param>
    /// <param name="dueScope">The scope it is owed to, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The promise as recorded, or the race that stopped it.</returns>
    public async ValueTask<PromiseOutcome> RegisterAsync(
        string versionId,
        string key,
        string kind,
        string description,
        string? originScope = null,
        string? dueScope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        return await AppendAsync(
            versionId,
            new Promise
            {
                Id = LedgerIds.New(),
                VersionId = versionId,
                Key = key,
                Kind = kind,
                Description = description,
                OriginScope = originScope,
                DueScope = dueScope,
                Status = PromiseStatus.Open,
                // Overwritten by the append with the position it settles at; a promise carries no position until it
                // has one, and the store is the only thing that can hand one out.
                Sequence = SequenceNumber.Zero,
            },
            PromiseCodec.RegisteredEventType,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fulfils the first open promise with a key.
    /// </summary>
    /// <param name="versionId">The version it is fulfilled in.</param>
    /// <param name="key">The coordination key to fulfil.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The promise as fulfilled, the fact that none was open, or the race that stopped it.</returns>
    /// <remarks>
    /// Only an open promise is fulfilled, and only the first: fulfilling one that is already fulfilled would record
    /// a second fulfilment for an obligation that was settled, and the plane would then say it was fulfilled twice.
    /// </remarks>
    public async ValueTask<FulfilOutcome> FulfilAsync(
        string versionId,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var snapshot = await _snapshots
            .BuildAsync(versionId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var open = snapshot.Promises.FirstOrDefault(promise =>
            string.Equals(promise.Key, key, StringComparison.Ordinal) &&
            promise.Status is PromiseStatus.Open);

        FulfilOutcome outcome = open is null
            ? new PromiseNotOpen(key)
            : await AppendFulfilmentAsync(versionId, open, cancellationToken).ConfigureAwait(false);

        return outcome;
    }

    private async ValueTask<FulfilOutcome> AppendFulfilmentAsync(
        string versionId,
        Promise open,
        CancellationToken cancellationToken) =>
        await AppendAsync(versionId, open, PromiseCodec.FulfilledEventType, cancellationToken)
            .ConfigureAwait(false) switch
        {
            Promise fulfilled => fulfilled,
            WriteContended contended => contended,
        };

    // The event's position is the head it is appended at, re-read on every attempt: a promise that lost the race is
    // retried rather than mis-stamped, because a position a write did not settle at is a position no pin can see.
    private async ValueTask<PromiseOutcome> AppendAsync(
        string versionId,
        Promise promise,
        string eventType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var stream = StreamId.From(versionId);
        var lastExpected = SequenceNumber.Zero;
        var lastActual = SequenceNumber.Zero;
        var fulfilling = eventType == PromiseCodec.FulfilledEventType;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var head = await _storage.HeadAsync(stream, cancellationToken).ConfigureAwait(false);
            lastExpected = head;

            var position = new SequenceNumber(head.Value + 1);
            var recorded = fulfilling
                ? promise with { Status = PromiseStatus.Fulfilled, Sequence = position, FulfilledSequence = position }
                : promise with { Sequence = position };

            var result = await _storage
                .AppendAsync(stream, head, [new LedgerEvent(eventType, PromiseCodec.Encode(recorded))], cancellationToken)
                .ConfigureAwait(false);

            if (result is Appended appended)
            {
                return fulfilling
                    ? recorded with { Sequence = appended.Head, FulfilledSequence = appended.Head }
                    : recorded with { Sequence = appended.Head };
            }

            if (result is VersionConflict conflict)
            {
                lastExpected = conflict.Expected;
                lastActual = conflict.Actual;
            }
        }

        return new WriteContended(lastExpected, lastActual);
    }
}
