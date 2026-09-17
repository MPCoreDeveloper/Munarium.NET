namespace Munarium.Claims;

/// <summary>
/// Whether an anchor still holds a detail to its locked value.
/// </summary>
/// <remarks>
/// Only a locked anchor is judged against. A released anchor stays in the ledger as the record that
/// the detail was once pinned, which is why the lock is a status rather than a deletion.
/// </remarks>
public enum AnchorStatus
{
    /// <summary>The anchor binds its detail: a claim that contradicts it is blocked.</summary>
    Locked = 0,

    /// <summary>The lock was lifted; the anchor is history.</summary>
    Released = 1,
}
