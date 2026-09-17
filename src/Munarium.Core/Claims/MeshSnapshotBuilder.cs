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

        // One read of the version's stream: the planes share it, so four reads to answer one question would
        // be four round trips per gate evaluation - and the gates run on the write path.
        var entries = await _storage
            .ReadAsync(StreamId.From(versionId), SequenceNumber.Zero, cancellationToken)
            .ConfigureAwait(false);

        var planes = Fold(entries, asOf);

        return new MeshSnapshot
        {
            VersionId = versionId,
            Facts = current,
            // The ladder is rebuilt from the facts the snapshot holds rather than read from storage: a
            // stored rung is an upsert with no history, so it cannot answer "what did this scope say in
            // March" - only the pinned facts can, and the rungs are a function of them.
            Digests = DigestLadder.Build(versionId, current),
            Anchors = planes.Anchors,
            Promises = planes.Promises,
            Counters = planes.Counters,
            Entities = planes.Entities,
            AsOfSequence = asOf,
        };
    }

    /// <summary>
    /// Folds a version's stream into the planes as they stand at a pin.
    /// </summary>
    /// <remarks>
    /// The fold is over the events at or below the pin, which is what delivers every plane's pin semantics
    /// at once: a release recorded after the pin leaves the lock standing, a fulfilment recorded after the
    /// pin leaves the promise open, and a counter or an entity recorded after the pin is not there yet. That
    /// is the same property a supersession has, and it comes out of one rule instead of one rule per plane.
    /// <para>
    /// Two of the four planes are keyed and two are not by accident. Anchors are keyed by the detail they
    /// lock, promises by their stable coordination key, counters by the pattern they count, entities by their
    /// identity - and in every case the later event for a key replaces the earlier one, so a plane is a state
    /// and not a log. A released anchor is dropped rather than carried, because the snapshot's contract is the
    /// locked ones and a gate that has to second-guess an entry can get it wrong.
    /// </para>
    /// </remarks>
    private static Planes Fold(IReadOnlyList<LedgerEntry> entries, SequenceNumber pin)
    {
        var anchors = new SortedDictionary<string, Anchor>(StringComparer.Ordinal);
        var promises = new SortedDictionary<string, Promise>(StringComparer.Ordinal);
        var counters = new SortedDictionary<string, CounterTotal>(StringComparer.Ordinal);
        var entities = new SortedDictionary<string, Entity>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.GlobalSequence > pin)
            {
                continue;
            }

            var payload = entry.Event.Payload.Span;

            switch (entry.Event.Type)
            {
                case AnchorCodec.LockedEventType:
                    var anchor = AnchorCodec.Decode(payload, entry.GlobalSequence);
                    anchors[anchor.DetailKey] = anchor;
                    break;

                case AnchorCodec.ReleasedEventType:
                    anchors.Remove(AnchorCodec.Decode(payload, entry.GlobalSequence).DetailKey);
                    break;

                case PromiseCodec.RegisteredEventType:
                    var registered = PromiseCodec.DecodeRegistered(payload, entry.GlobalSequence);
                    promises[registered.Key] = registered;
                    break;

                case PromiseCodec.FulfilledEventType:
                    var fulfilled = PromiseCodec.DecodeFulfilled(payload, entry.GlobalSequence);
                    promises[fulfilled.Key] = fulfilled;
                    break;

                case CounterCodec.RecordedEventType:
                    var counter = CounterCodec.Decode(payload);
                    counters[counter.Key] = counter;
                    break;

                case EntityCodec.ResolvedEventType:
                    var entity = EntityCodec.Decode(payload, entry.GlobalSequence);
                    entities[entity.Id] = entity;
                    break;

                default:
                    break;
            }
        }

        return new Planes(anchors, [.. promises.Values], [.. counters.Values], [.. entities.Values]);
    }

    /// <summary>
    /// The planes a fold produced, in the shape the snapshot wants them.
    /// </summary>
    private sealed record Planes(
        IReadOnlyDictionary<string, Anchor> Anchors,
        IReadOnlyList<Promise> Promises,
        IReadOnlyList<CounterTotal> Counters,
        IReadOnlyList<Entity> Entities);
}
