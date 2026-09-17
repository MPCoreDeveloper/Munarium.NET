namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// The point-in-time view the gates and the composer read: one pin bounds everything at once.
/// </summary>
/// <remarks>
/// Facts, anchors, digests, promises, counters and entities share one sequence axis, which is the
/// whole reason a pin can be a single number: a reader that pinned facts and promises separately would
/// have to invent a rule for the two pins disagreeing.
/// <para>
/// <see cref="Facts"/> is expected to be seq-ascending and already resolved-current (see
/// <see cref="ClaimResolution"/>), and <see cref="Anchors"/> is expected to carry locked anchors only,
/// keyed by detail key with the later version winning. Both are contracts of the snapshot rather than
/// things the gates re-derive, so a backend that builds a snapshot has to meet them.
/// </para>
/// </remarks>
public sealed record MeshSnapshot
{
    /// <summary>Gets the version the snapshot was taken of.</summary>
    public string VersionId { get; init; } = string.Empty;

    /// <summary>Gets the current facts, in ascending sequence order.</summary>
    public IReadOnlyList<Claim> Facts { get; init; } = [];

    /// <summary>Gets the locked anchors, keyed by detail key (<c>subject.key</c>).</summary>
    public IReadOnlyDictionary<string, Anchor> Anchors { get; init; } =
        new SortedDictionary<string, Anchor>(StringComparer.Ordinal);

    /// <summary>Gets the digest rungs as they stand at the pin.</summary>
    public IReadOnlyList<Digest> Digests { get; init; } = [];

    /// <summary>Gets the promises, with their status as of the pin.</summary>
    public IReadOnlyList<Promise> Promises { get; init; } = [];

    /// <summary>Gets the whole-document counters.</summary>
    public IReadOnlyList<CounterTotal> Counters { get; init; } = [];

    /// <summary>Gets the resolved entities.</summary>
    public IReadOnlyList<Entity> Entities { get; init; } = [];

    /// <summary>Gets the sequence the snapshot was pinned at, when it was pinned.</summary>
    public SequenceNumber? AsOfSequence { get; init; }

    /// <summary>Gets the calendar date the snapshot was read as of, when a date pin was used.</summary>
    public string? AsOfDate { get; init; }

    /// <summary>
    /// Gets the instant the newest identity in the snapshot was created at.
    /// </summary>
    /// <remarks>
    /// The ledger's identities are ULIDs, so a snapshot carries its own time: this is derived from the
    /// facts, anchors, promises and entities it holds rather than passed in, and it cannot disagree with
    /// the ids it came from. A gate that reasons about "now" reads this, which is what makes its verdict
    /// reproducible by anyone holding the snapshot - a clock read would leave the reader unable to rebuild
    /// the answer later.
    /// <para>
    /// This is not the pin. A pin is a <em>position</em> in the ledger, and the two are deliberately
    /// different things: the pin says which writes are visible, this says when the newest visible write
    /// happened. <see langword="null"/> when no identity in the snapshot is a ULID - a caller may name its
    /// own claims - in which case a clock-driven rule needs an explicit date instead.
    /// </para>
    /// </remarks>
    public DateTimeOffset? WrittenAt
    {
        get
        {
            DateTimeOffset? newest = null;

            foreach (var identity in Identities())
            {
                if (LedgerIds.InstantOf(identity) is { } instant && (newest is null || instant > newest))
                {
                    newest = instant;
                }
            }

            return newest;
        }
    }

    /// <summary>
    /// Gets <see cref="WrittenAt"/> as the UTC calendar date the chronology rules read.
    /// </summary>
    public DateOnly? WrittenOn =>
        WrittenAt is { } instant ? DateOnly.FromDateTime(instant.UtcDateTime) : null;

    /// <summary>
    /// Enumerates every identity the snapshot carries.
    /// </summary>
    /// <remarks>
    /// Digests are left out because a rung is rebuilt rather than identified: it has a content hash, which
    /// is a hash of what it says and not a moment.
    /// </remarks>
    /// <returns>The identities, in plane order.</returns>
    private IEnumerable<string> Identities()
    {
        foreach (var fact in Facts)
        {
            yield return fact.Id;
        }

        foreach (var anchor in Anchors.Values)
        {
            yield return anchor.Id;
        }

        foreach (var promise in Promises)
        {
            yield return promise.Id;
        }

        foreach (var entity in Entities)
        {
            yield return entity.Id;
        }
    }

    /// <summary>
    /// Gets the highest position any fact in the snapshot holds, or zero when there is none.
    /// </summary>
    /// <remarks>
    /// The bound an evaluation inherits: a gate that reasons about "now" reads it rather than calling
    /// a clock, which is what keeps a verdict reproducible from the snapshot alone.
    /// </remarks>
    public SequenceNumber MaxSequence =>
        Facts.Count == 0 ? SequenceNumber.Zero : Facts.Max(fact => fact.Sequence);
}
