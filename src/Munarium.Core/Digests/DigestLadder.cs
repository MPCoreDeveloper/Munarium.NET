namespace Munarium.Digests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Munarium.Claims;
using Munarium.Ledger;

/// <summary>
/// The digest ladder: deterministic, model-free compression rungs over the fact ledger.
/// </summary>
/// <remarks>
/// Tier 0 is one rung per scope - the scope's current facts as canonical lines. Tier 1 is one rung per
/// scope-prefix group, with the values elided and the claim keys kept, so a reader can see what a group
/// is about without reading it. Tier 2 is the rollup over the whole lineage.
/// <para>
/// Rungs are <em>rebuilt</em> rather than served under a pin. Stored digest text is an upsert with no
/// history, so it cannot answer "what did this scope say in March" - only facts can, and the ladder is
/// therefore a pure function of the pinned facts. The rungs are model-free for the same reason: a
/// summary a model wrote is not reproducible, and a rung that cannot be reproduced cannot be pinned.
/// </para>
/// </remarks>
public static class DigestLadder
{
    /// <summary>The labels of the three tiers, in tier order.</summary>
    public static readonly IReadOnlyList<string> TierLabels = ["scope", "group", "rollup"];

    /// <summary>
    /// Resolves the group a scope belongs to: its first dotted segment.
    /// </summary>
    /// <remarks>
    /// One level, not a configurable depth: the group is meant to be the coarse handle - the document,
    /// or the work - and a rule that could go deeper would make two tiers answer the same question.
    /// </remarks>
    /// <param name="scope">The scope.</param>
    /// <returns>The group name.</returns>
    public static string GroupOf(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var separator = scope.IndexOf('.', StringComparison.Ordinal);
        return separator < 0 ? scope : scope[..separator];
    }

    /// <summary>
    /// Builds the tier-0 rungs: one per scope, holding the scope's facts as canonical lines.
    /// </summary>
    /// <param name="versionId">The version the rungs are built for.</param>
    /// <param name="facts">The current facts, in ascending sequence order.</param>
    /// <returns>The rungs, ordered by scope.</returns>
    public static IReadOnlyList<Digest> Tier0(string versionId, IReadOnlyList<Claim> facts)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        ArgumentNullException.ThrowIfNull(facts);

        var rungs = new List<Digest>();

        foreach (var (scope, claims) in ByScope(facts))
        {
            var content = string.Create(
                CultureInfo.InvariantCulture,
                $"[{scope}] {claims.Count} facts\n{string.Join('\n', claims.Select(claim => claim.NormalizedText))}");

            rungs.Add(Rung(versionId, tier: 0, scope, content, BuiltFrom(claims)));
        }

        return rungs;
    }

    /// <summary>
    /// Builds the tier-1 rungs: one per scope-prefix group, with keys only and values elided.
    /// </summary>
    /// <param name="versionId">The version the rungs are built for.</param>
    /// <param name="facts">The current facts, in ascending sequence order.</param>
    /// <returns>The rungs, ordered by group.</returns>
    public static IReadOnlyList<Digest> Tier1(string versionId, IReadOnlyList<Claim> facts)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        ArgumentNullException.ThrowIfNull(facts);

        var byGroup = new SortedDictionary<string, List<Claim>>(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            var group = GroupOf(fact.ScopePath ?? string.Empty);

            if (!byGroup.TryGetValue(group, out var groupClaims))
            {
                groupClaims = [];
                byGroup[group] = groupClaims;
            }

            groupClaims.Add(fact);
        }

        var rungs = new List<Digest>();

        foreach (var (group, claims) in byGroup)
        {
            // Sorted and deduplicated so the rung's content - and therefore its hash - is a function of
            // which keys the group holds, not of the order they happened to be written in.
            var keys = claims
                .Select(claim => claim.ClaimKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal);

            var content = string.Create(
                CultureInfo.InvariantCulture,
                $"[group {group}] {claims.Count} facts across {ScopeCount(claims)} scopes; keys: {string.Join(", ", keys)}");

            rungs.Add(Rung(versionId, tier: 1, group, content, BuiltFrom(claims)));
        }

        return rungs;
    }

    /// <summary>
    /// Builds the tier-2 rung: the whole-lineage rollup.
    /// </summary>
    /// <param name="versionId">The version the rung is built for.</param>
    /// <param name="facts">The current facts, in ascending sequence order.</param>
    /// <returns>The rollup rung.</returns>
    public static Digest Tier2(string versionId, IReadOnlyList<Claim> facts)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        ArgumentNullException.ThrowIfNull(facts);

        var subjects = facts
            .Select(fact => fact.Subject)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(subject => subject, StringComparer.Ordinal)
            .ToList();

        var content = string.Create(
            CultureInfo.InvariantCulture,
            $"[rollup] {facts.Count} facts, {ScopeCount(facts)} scopes, {subjects.Count} subjects: {string.Join(", ", subjects)}");

        return Rung(versionId, tier: 2, string.Empty, content, BuiltFrom(facts));
    }

    /// <summary>
    /// Builds the full ladder from facts that are already resolved at a pin.
    /// </summary>
    /// <remarks>
    /// The caller resolves first on purpose: the ladder takes facts that are current, not a lineage's
    /// raw history, so a rung can never include a value that was superseded at the pin it is built for.
    /// </remarks>
    /// <param name="versionId">The version the rungs are built for.</param>
    /// <param name="facts">The pin-resolved facts.</param>
    /// <returns>Tier 0, then tier 1, then the rollup.</returns>
    public static IReadOnlyList<Digest> Build(string versionId, IReadOnlyList<Claim> facts)
    {
        var ladder = new List<Digest>(Tier0(versionId, facts));
        ladder.AddRange(Tier1(versionId, facts));
        ladder.Add(Tier2(versionId, facts));
        return ladder;
    }

    private static SortedDictionary<string, List<Claim>> ByScope(IReadOnlyList<Claim> facts)
    {
        var byScope = new SortedDictionary<string, List<Claim>>(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            var scope = fact.ScopePath ?? string.Empty;

            if (!byScope.TryGetValue(scope, out var claims))
            {
                claims = [];
                byScope[scope] = claims;
            }

            claims.Add(fact);
        }

        return byScope;
    }

    private static int ScopeCount(IReadOnlyList<Claim> facts) => facts
        .Select(fact => fact.ScopePath)
        .Where(scope => scope is not null)
        .Distinct(StringComparer.Ordinal)
        .Count();

    private static SequenceNumber BuiltFrom(IReadOnlyList<Claim> claims) =>
        claims.Count == 0 ? SequenceNumber.Zero : claims.Max(claim => claim.Sequence);

    private static Digest Rung(string versionId, int tier, string scopePath, string content, SequenceNumber builtFrom) =>
        new()
        {
            VersionId = versionId,
            Tier = tier,
            ScopePath = scopePath,
            Content = content,
            ContentHash = Hash(content),
            BuiltFromSequence = builtFrom,
        };

    private static string Hash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
