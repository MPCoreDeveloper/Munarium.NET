namespace Munarium.Sessions;

using Munarium.Ledger;

/// <summary>
/// The identity a session is created under.
/// </summary>
/// <remarks>
/// The original mints <c>ses-</c> plus a version-7 UUID, and the property it is after is that the value sorts by when
/// the session started. This mints the port's own ordering primitive for the same reason - a ULID carries its instant -
/// and for one more: the store's predicates are built over identities rather than over caller-supplied text, because a
/// predicate over the latter matches nothing (measured), so a session id that is a ULID is a session id a table can be
/// asked about.
/// </remarks>
public static class SessionIds
{
    /// <summary>The prefix every session identity carries.</summary>
    public const string Prefix = "ses-";

    /// <summary>Mints an identity.</summary>
    /// <returns>The identity, as <c>ses-</c> plus a 26-character ULID.</returns>
    public static string New() => string.Concat(Prefix, LedgerIds.New());

    /// <summary>Answers whether a value has the shape of a session identity.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether it is one.</returns>
    public static bool IsSessionId(string? value) =>
        value is not null
        && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.Length == Prefix.Length + 26;
}
