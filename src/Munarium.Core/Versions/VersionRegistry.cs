namespace Munarium.Versions;

using Munarium.Facts;
using Munarium.Ledger;

/// <summary>
/// The versions a pin knows, and the lineage they form.
/// </summary>
/// <remarks>
/// Built from a fact slice, so it is exactly as reproducible as the slice it came from: the same pin
/// rebuilds the same graph, and nothing about a version lives outside the ledger that recorded it.
/// </remarks>
public sealed class VersionRegistry
{
    /// <summary>The shape a version is claimed under.</summary>
    public const string ShapeName = "version";

    private readonly Dictionary<string, VersionRecord> _versions;
    private readonly Dictionary<string, SequenceNumber> _pins;

    private VersionRegistry(
        Dictionary<string, VersionRecord> versions,
        Dictionary<string, SequenceNumber> pins)
    {
        _versions = versions;
        _pins = pins;
    }

    /// <summary>Gets the number of versions that are current at the pin.</summary>
    public int Count => _versions.Count;

    /// <summary>Gets the versions, ordered by identity so an answer is deterministic.</summary>
    public IReadOnlyList<VersionRecord> Versions =>
        [.. _versions.Values.OrderBy(version => version.VersionId, StringComparer.Ordinal)];

    /// <summary>
    /// Reads every version that is current at a pin.
    /// </summary>
    /// <param name="slice">The slice to read.</param>
    /// <returns>The registry.</returns>
    public static VersionRegistry At(FactSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var versions = new Dictionary<string, VersionRecord>(StringComparer.Ordinal);
        var pins = new Dictionary<string, SequenceNumber>(StringComparer.Ordinal);

        foreach (var sliced in slice.Facts)
        {
            if (!IsVersionLineage(sliced.Fact.Lineage))
            {
                continue;
            }

            if (VersionRecord.FromBody(sliced.Fact.Body) is { } version)
            {
                versions[version.VersionId] = version;
                pins[version.VersionId] = sliced.GlobalSequence;
            }
        }

        return new VersionRegistry(versions, pins);
    }

    /// <summary>Tries to resolve a version by identity.</summary>
    /// <param name="versionId">The version identity.</param>
    /// <param name="version">The version, when it is known at this pin.</param>
    /// <returns><see langword="true"/> when the version is known.</returns>
    public bool TryResolve(string versionId, out VersionRecord? version) =>
        _versions.TryGetValue(versionId, out version);

    /// <summary>Tries to resolve the global position a version was recorded at.</summary>
    /// <param name="versionId">The version identity.</param>
    /// <param name="pin">The global position of the claim that created it.</param>
    /// <returns><see langword="true"/> when the version is known.</returns>
    public bool TryPin(string versionId, out SequenceNumber pin) => _pins.TryGetValue(versionId, out pin);

    /// <summary>The path from a lineage root down to a version, inclusive.</summary>
    /// <param name="versionId">The version identity.</param>
    /// <returns>
    /// The lineage, or a single element when only that version is known. A parent that is not known at this
    /// pin ends the walk rather than failing it, because a pin is allowed to precede a parent - and a
    /// version that names itself as an ancestor ends it too.
    /// </returns>
    public IReadOnlyList<VersionRecord> LineageOf(string versionId)
    {
        if (!_versions.TryGetValue(versionId, out var version))
        {
            return [];
        }

        var path = new List<VersionRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (version is not null && seen.Add(version.VersionId))
        {
            path.Add(version);
            version = string.IsNullOrEmpty(version.ParentVersionId)
                ? null
                : _versions.GetValueOrDefault(version.ParentVersionId);
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// The version in effect on a date: the latest one whose as-of date is on or before it.
    /// </summary>
    /// <param name="date">The date to resolve.</param>
    /// <returns>The version, or <see langword="null"/> when no version reaches that far back.</returns>
    public VersionRecord? EffectiveOn(DateOnly date)
    {
        VersionRecord? effective = null;

        foreach (var version in _versions.Values)
        {
            if (version.AsOfDate is not { } asOf || asOf > date)
            {
                continue;
            }

            if (effective?.AsOfDate is not { } current ||
                asOf > current ||
                (asOf == current && string.CompareOrdinal(version.VersionId, effective.VersionId) > 0))
            {
                effective = version;
            }
        }

        return effective;
    }

    private static bool IsVersionLineage(string lineage) =>
        lineage.StartsWith(ShapeName + "@", StringComparison.Ordinal);
}
