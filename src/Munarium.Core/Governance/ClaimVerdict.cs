namespace Munarium.Governance;

/// <summary>
/// Every gate permitted the claim; it may be recorded as asserted.
/// </summary>
public sealed record Permitted
{
    /// <summary>
    /// Gets the permitted verdict. A marker case carries no data, so a single instance serves all.
    /// </summary>
    public static Permitted Instance { get; } = new();
}

/// <summary>
/// A gate refused the claim. The claim is recorded as disputed, never dropped.
/// </summary>
/// <param name="Gate">The gate that refused.</param>
/// <param name="Reason">Why it refused, in the operator's words.</param>
public sealed record Blocked(string Gate, string Reason);

/// <summary>
/// The governance verdict for a claim, modelled as a C# 15 union of exactly these two outcomes.
/// </summary>
/// <remarks>
/// A gate cannot "throw to reject": a rejection is data the ledger has to record, so it travels
/// back as a value that the write path must handle. Forgetting a case does not compile.
/// </remarks>
public readonly union ClaimVerdict(Permitted, Blocked);
