namespace Munarium.Claims;

/// <summary>
/// What the ledger did with a claim: it was accepted, or a gate blocked it and it was recorded as
/// disputed.
/// </summary>
/// <remarks>
/// There is deliberately no third case. A claim a gate refuses is still written - as disputed - so the
/// ledger carries the claim and the refusal together. "Dropped" would be a state the ledger could not
/// report, and a refusal that cannot be recorded is not governance.
/// </remarks>
public enum ClaimStatus
{
    /// <summary>The claim passed every gate.</summary>
    Accepted = 0,

    /// <summary>A gate blocked the claim. It is recorded, never dropped.</summary>
    Disputed = 1,
}
