namespace Munarium.Claims;

using Munarium.Digests;
using Munarium.Facts;
using Munarium.Ledger;

/// <summary>
/// Assembles the pinned view the gates and the composer read.
/// </summary>
/// <remarks>
/// This is the seam the "no backend builds a snapshot" gap closes through: a gate that assembled its own
/// view would be a second definition of what the ledger holds at a pin, and the two would drift.
/// <para>
/// The fact, anchor and promise planes are read here; counters and entities are in the kernel as types and
/// as logic but have no plane to read yet, so a snapshot carries empty ones until they do. The digests are
/// not read at all - they are rebuilt from the pinned facts, because a stored rung has no history.
/// </para>
/// </remarks>
/// <param name="storage">The ledger's storage seam, which every plane is read from.</param>
public sealed class MeshSnapshotBuilder(IStorageBackend storage)
{
    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly FactLedger _facts = new(storage);

    /// <summary>
    /// Builds the snapshot at a pin, or at the current one.
    /// </summary>
    /// <param name="versionId">
    /// The version to read, or an empty string for every version. A gate judges one version's write path,
    /// so a caller that names a version gets that version's facts.
    /// </param>
    /// <param name="pin">The position to read as of, or <see langword="null"/> for the present.</param>
    /// <param name="scopePrefix">The scope to read, or <see langword="null"/> for every scope.</param>
    /// <param name="factLimit">How many facts to keep, counting from the newest, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot, with its facts resolved-current at the pin and its digest ladder rebuilt.</returns>
    public async ValueTask<MeshSnapshot> BuildAsync(
        string versionId,
        SequenceNumber? pin = null,
        string? scopePrefix = null,
        int? factLimit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionId);

        var asOf = pin ?? await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false);
        var slice = await _facts.SliceAsync(asOf, versionId, cancellationToken).ConfigureAwait(false);

        var current = slice.Facts
            .Select(sliced => ClaimProjection.Of(sliced.Fact, sliced.GlobalSequence))
            .ToList();

        // The scope filter and the limit are applied AFTER resolution, which is the only order that answers
        // "the newest two facts of this scope": filtering first would let a superseded fact occupy a slot,
        // and the reader would get fewer current facts than it asked for.
        if (scopePrefix is { Length: > 0 } prefix)
        {
            current = [.. current.Where(claim => ClaimScope.Matches(claim.ScopePath, prefix))];
        }

        if (factLimit is { } limit && current.Count > limit)
        {
            current = [.. current.OrderBy(claim => claim.Sequence.Value).TakeLast(limit)];
        }

        return new MeshSnapshot
        {
            VersionId = versionId,
            Facts = current,
            // The ladder is rebuilt from the facts the snapshot holds rather than read from storage: a
            // stored rung is an upsert with no history, so it cannot answer "what did this scope say in
            // March" - only the pinned facts can, and the rungs are a function of them.
            Digests = DigestLadder.Build(versionId, current),
            Anchors = await AnchorsAsync(versionId, asOf, cancellationToken).ConfigureAwait(false),
            Promises = await PromisesAsync(versionId, asOf, cancellationToken).ConfigureAwait(false),
            AsOfSequence = asOf,
        };
    }

    /// <summary>
    /// Folds a version's stream into the anchors that are locked at a pin.
    /// </summary>
    /// <remarks>
    /// The stream is folded rather than queried because the planes share the claims' stream: a lock, a
    /// release and a promise are events in it like anything else. The fold is over the events at or below the
    /// pin, so a release recorded after the pin leaves the lock standing - which is the same property a
    /// superseded claim has.
    /// <para>
    /// A released anchor is dropped rather than carried with a released status, because the snapshot's
    /// contract is the locked ones: a gate reads it to know what may not drift, and an entry it has to
    /// second-guess is an entry it can get wrong.
    /// </para>
    /// </remarks>
    private async ValueTask<IReadOnlyDictionary<string, Anchor>> AnchorsAsync(
        string versionId,
        SequenceNumber pin,
        CancellationToken cancellationToken)
    {
        var entries = await _storage
            .ReadAsync(StreamId.From(versionId), SequenceNumber.Zero, cancellationToken)
            .ConfigureAwait(false);

        var locked = new SortedDictionary<string, Anchor>(StringComparer.Ordinal);

        foreach (var entry in entries.Where(entry => entry.GlobalSequence <= pin))
        {
            if (AnchorCodec.IsAnchorEvent(entry.Event.Type))
            {
                var anchor = AnchorCodec.Decode(entry.Event.Payload.Span, entry.GlobalSequence);

                if (anchor.Status is AnchorStatus.Locked)
                {
                    locked[anchor.DetailKey] = anchor;
                }
                else
                {
                    locked.Remove(anchor.DetailKey);
                }
            }
        }

        return locked;
    }

    /// <summary>
    /// Folds a version's stream into the promises as they stand at a pin.
    /// </summary>
    /// <remarks>
    /// Keyed by the stable coordination key, so a promise restated is the same promise and not a second one.
    /// A fulfilment recorded after the pin does not apply, which is what makes a promise fulfilled later read
    /// back <em>open</em> at the earlier pin - the semantic the promise registry documents, delivered here by
    /// the fold rather than by a second status computation.
    /// </remarks>
    private async ValueTask<IReadOnlyList<Promise>> PromisesAsync(
        string versionId,
        SequenceNumber pin,
        CancellationToken cancellationToken)
    {
        var entries = await _storage
            .ReadAsync(StreamId.From(versionId), SequenceNumber.Zero, cancellationToken)
            .ConfigureAwait(false);

        var promises = new SortedDictionary<string, Promise>(StringComparer.Ordinal);

        foreach (var entry in entries.Where(entry => entry.GlobalSequence <= pin))
        {
            switch (entry.Event.Type)
            {
                case PromiseCodec.RegisteredEventType:
                    var registered = PromiseCodec.DecodeRegistered(entry.Event.Payload.Span, entry.GlobalSequence);
                    promises[registered.Key] = registered;
                    break;

                case PromiseCodec.FulfilledEventType:
                    var fulfilled = PromiseCodec.DecodeFulfilled(entry.Event.Payload.Span, entry.GlobalSequence);
                    promises[fulfilled.Key] = fulfilled;
                    break;

                default:
                    break;
            }
        }

        return [.. promises.Values];
    }
}
