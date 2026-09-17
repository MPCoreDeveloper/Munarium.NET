namespace Munarium.Claims;

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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot, with its facts resolved-current at the pin.</returns>
    public async ValueTask<MeshSnapshot> BuildAsync(
        string versionId,
        SequenceNumber? pin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionId);

        var asOf = pin ?? await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false);
        var slice = await _facts.SliceAsync(asOf, versionId, cancellationToken).ConfigureAwait(false);

        return new MeshSnapshot
        {
            VersionId = versionId,
            Facts =
            [
                .. slice.Facts.Select(sliced => ClaimProjection.Of(sliced.Fact, sliced.GlobalSequence)),
            ],
            AsOfSequence = asOf,
        };
    }
}
