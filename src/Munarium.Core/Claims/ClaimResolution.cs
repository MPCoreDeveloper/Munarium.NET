namespace Munarium.Claims;

/// <summary>
/// Pin-aware supersession resolution: which claims are current out of a lineage's raw history.
/// </summary>
/// <remarks>
/// This is the reference implementation of the ledger's read semantics; every storage backend's query
/// has to agree with it, which is why it is a pure function over claims in any order and not a piece
/// of SQL.
/// <para>
/// The load-bearing rule is that the superseded set is itself filtered by the pin: a claim that was
/// superseded only <em>after</em> the pin still reads as current at the pin. The naive reading - ask
/// which claim is newest, then check the pin - answers a different question, and answers it wrongly
/// for every pin but the head.
/// </para>
/// </remarks>
public static class ClaimResolution
{
    /// <summary>
    /// Resolves the claims that are current under a query.
    /// </summary>
    /// <param name="claims">The lineage's claims, in any order.</param>
    /// <param name="query">The query; <see langword="null"/> reads every accepted claim at the head.</param>
    /// <returns>The current claims, in ascending sequence order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <see cref="ClaimQuery.Limit"/> is negative.</exception>
    public static IReadOnlyList<Claim> Resolve(IEnumerable<Claim> claims, ClaimQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var request = query ?? new ClaimQuery();
        if (request.Limit is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                request.Limit,
                "A limit cannot be negative; use null for no limit.");
        }

        IReadOnlyList<ClaimStatus> statuses = request.Statuses.Count == 0
            ? [ClaimStatus.Accepted]
            : request.Statuses;

        // 1. The pin. Materialised once, because both the superseded set and the visible set read it.
        List<Claim> pinned = request.AsOfSequence is { } pin
            ? [.. claims.Where(claim => claim.Sequence <= pin)]
            : [.. claims];

        // 2. The superseded set, taken from the PINNED view and from any status: a correction that a
        //    gate disputed still supersedes the value it was correcting.
        var superseded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in pinned)
        {
            if (claim.SupersedesId is { } target)
            {
                superseded.Add(target);
            }
        }

        // 3. Visible: the requested statuses, nothing superseded at the pin, and in scope.
        var visible = pinned
            .Where(claim => statuses.Contains(claim.Status))
            .Where(claim => !superseded.Contains(claim.Id))
            .Where(claim => ClaimScope.Matches(claim.ScopePath, request.ScopePrefix))
            .OrderBy(claim => claim.Sequence.Value)
            .ToList();

        // 4. A limit keeps the NEWEST n; the answer stays in ascending sequence order.
        if (request.Limit is { } limit && visible.Count > limit)
        {
            visible.RemoveRange(0, visible.Count - limit);
        }

        return visible;
    }
}
