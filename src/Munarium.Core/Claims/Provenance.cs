namespace Munarium.Claims;

/// <summary>
/// How a claim came to exist, which is a different question from how it was judged.
/// </summary>
/// <remarks>
/// Provenance decides the write path's mode rather than its verdict: a corpus absorbed after the fact
/// reads the same contradictions a live one does, but it cannot block on them, so the blocks are
/// downgraded to warnings (see the gate runner's downgrade). Keeping the two axes apart is what lets
/// the same gates judge a backfill and a live claim without being weakened for either.
/// </remarks>
public enum Provenance
{
    /// <summary>Observed on the write path as it happened. The default.</summary>
    Witnessed = 0,

    /// <summary>Absorbed from a corpus that already existed, so its contradictions inform rather than block.</summary>
    Backfilled = 1,

    /// <summary>A value restored to what the ledger held before it was corrupted.</summary>
    Repaired = 2,

    /// <summary>Derived by the system rather than asserted by an actor.</summary>
    Emergent = 3,

    /// <summary>Written to close a gap in coverage the ledger had already recorded.</summary>
    CoverageRepair = 4,
}
