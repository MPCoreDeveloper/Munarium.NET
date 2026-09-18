namespace Munarium.Idempotency;

using Munarium.Ledger;

/// <summary>
/// The scopes an operation is keyed in, and the rule a key has to satisfy.
/// </summary>
/// <remarks>
/// A key is a ULID, the same shape this port mints for everything else that has to be unique and sortable: it carries the
/// instant it was made, so an operator reading a key can tell how old a retry is, and a client can mint one without a
/// round trip. A scope names the operation and the subject, so one caller's key for one write cannot swallow another.
/// </remarks>
public static class IdempotencyKeys
{
    /// <summary>The scope of a single claim write.</summary>
    public const string Claim = "claims";

    /// <summary>The scope of a claim batch write.</summary>
    public const string ClaimBatch = "claim-batches";

    /// <summary>
    /// Builds the scope of an operation on a version.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="versionId">The version it was asked of.</param>
    /// <returns>The scope.</returns>
    public static string Of(string operation, string versionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        return string.Concat(operation, "/", versionId ?? string.Empty);
    }

    /// <summary>
    /// Checks a caller's key, answering why it cannot be used.
    /// </summary>
    /// <param name="key">The key, or <see langword="null"/> when the caller sent none.</param>
    /// <param name="invalid">Why the key cannot be used, or <see langword="null"/> when it can - including when none was sent.</param>
    /// <returns><see langword="true"/> when the key is one this port can key a command by.</returns>
    public static bool TryAccept(string? key, out string? invalid)
    {
        var trimmed = (key ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            invalid = null;

            return false;
        }

        if (!LedgerIds.IsLedgerId(trimmed))
        {
            invalid = $"idempotency_key '{trimmed}' is not a ULID; a key has to be mintsable by a client without a "
                + "round trip and sortable by the instant it was made.";

            return false;
        }

        invalid = null;

        return true;
    }
}
