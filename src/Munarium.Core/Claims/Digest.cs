namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// One rung of the digest ladder: a deterministic, model-free compression of the facts under a scope.
/// </summary>
/// <remarks>
/// The ladder is what a later reader gets instead of the facts themselves, so it has to be rebuilt
/// rather than served: under a pin the rungs are recomputed from the pinned facts, because stored
/// digest text has no history to read at a pin. <see cref="Tier"/> is 0 for a scope, 1 for a
/// scope-prefix group and 2 for the whole-lineage rollup.
/// </remarks>
public sealed record Digest
{
    /// <summary>Gets the version the rung was built for.</summary>
    public required string VersionId { get; init; }

    /// <summary>Gets the rung's tier: 0 per scope, 1 per group, 2 the rollup.</summary>
    public required int Tier { get; init; }

    /// <summary>Gets the scope the rung covers - the group name at tier 1, and empty for the rollup.</summary>
    public required string ScopePath { get; init; }

    /// <summary>Gets the rung's content.</summary>
    public required string Content { get; init; }

    /// <summary>Gets the SHA-256 of the content, as lowercase hex.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Gets the highest ledger position the rung was built from.</summary>
    public required SequenceNumber BuiltFromSequence { get; init; }
}
