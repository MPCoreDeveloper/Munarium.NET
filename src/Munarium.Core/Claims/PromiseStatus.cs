namespace Munarium.Claims;

/// <summary>
/// Where a promise stands: still owed, paid, past its scope unpaid, or broken.
/// </summary>
/// <remarks>
/// Expiry and violation are recorded rather than inferred so that a pin can report what the ledger
/// said at the time instead of what a reader would compute today.
/// </remarks>
public enum PromiseStatus
{
    /// <summary>Owed, and not yet due.</summary>
    Open = 0,

    /// <summary>Paid.</summary>
    Fulfilled = 1,

    /// <summary>Its due scope passed without the promise being paid.</summary>
    Expired = 2,

    /// <summary>Broken deliberately - the ledger holds a claim that says so.</summary>
    Violated = 3,
}
