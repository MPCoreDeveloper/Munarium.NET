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
/// The fact plane is what is stored today. Anchors, promises, counters, entities and the digest ladder are
/// in the kernel as types and as logic, but nothing reads them back out of storage yet, so a snapshot
/// built here carries facts and an as-of positional pin - and the planes arrive as their storage does.
/// </para>
/// </remarks>
/// <param name="facts">The ledger's fact read model.</param>
public sealed class MeshSnapshotBuilder(FactLedger facts)
{
    private readonly FactLedger _facts = facts ?? throw new ArgumentNullException(nameof(facts));

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
            AsOfSequence = asOf,
        };
    }
}
