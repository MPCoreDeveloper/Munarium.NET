namespace Munarium.Access;

/// <summary>One issued capability, as the audit holds it.</summary>
/// <remarks>
/// Never the token: a bearer credential is not stored anywhere, which is why revocation has to work from an identity
/// rather than from a string match. This row is what an operator reads to answer "who was given what, and is it still
/// live", and it is what a withdrawal names.
/// </remarks>
public sealed record IssuedCapability
{
    /// <summary>Gets the capability's identity, which is the claim a withdrawal names.</summary>
    public required string TokenId { get; init; }

    /// <summary>Gets the tenant it was minted for.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the subject it was minted for.</summary>
    public required string Subject { get; init; }

    /// <summary>Gets the level it carries.</summary>
    public required int Level { get; init; }

    /// <summary>Gets the compartments it carries.</summary>
    public IReadOnlyList<string> Compartments { get; init; } = [];

    /// <summary>Gets the scopes it carries.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Gets the runbook names it permits, or <see langword="null"/> for any.</summary>
    public IReadOnlyList<string>? Runbooks { get; init; }

    /// <summary>Gets when it was issued, in seconds since the epoch.</summary>
    public required long IssuedAt { get; init; }

    /// <summary>Gets when it stops being valid, in seconds since the epoch.</summary>
    public required long ExpiresAt { get; init; }

    /// <summary>Gets when it was withdrawn, or <see langword="null"/> while it stands.</summary>
    public long? RevokedAt { get; init; }

    /// <summary>Gets a value indicating whether it has been withdrawn.</summary>
    public bool Revoked => RevokedAt is not null;

    /// <summary>Reads issued claims as the row the audit keeps.</summary>
    /// <remarks>
    /// The mirror of <see cref="ToClaims"/>, and the reason both exist: what is recorded and what is signed have to be
    /// the same claims, or a withdrawal would name a capability that differs from the one in the token.
    /// </remarks>
    /// <param name="claims">The claims as issued.</param>
    /// <returns>The row.</returns>
    public static IssuedCapability FromClaims(AccessClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        return new IssuedCapability
        {
            TokenId = claims.TokenId,
            Tenant = claims.Tenant,
            Subject = claims.Subject,
            Level = claims.Level,
            Compartments = claims.Compartments,
            Scopes = claims.Scopes,
            Runbooks = claims.Runbooks,
            IssuedAt = claims.IssuedAt,
            ExpiresAt = claims.ExpiresAt,
        };
    }

    /// <summary>Reads an issued row back as claims, so a stored row and a signed token describe the same thing.</summary>
    /// <returns>The claims.</returns>
    public AccessClaims ToClaims() => new()
    {
        Subject = Subject,
        Tenant = Tenant,
        Level = Level,
        Compartments = Compartments,
        Scopes = Scopes,
        Runbooks = Runbooks,
        TokenId = TokenId,
        IssuedAt = IssuedAt,
        ExpiresAt = ExpiresAt,
    };
}

/// <summary>Where issued capabilities are recorded, and where a withdrawal is kept.</summary>
/// <remarks>
/// Withdrawal cannot be done by remembering the token: nothing stores token material, and a capability already handed out
/// cannot be recalled. So the row is the lever - a withdrawal marks it, and verification refuses a capability whose row
/// says so. That is why this is a seam and not a cache: a deny-list that a restart forgets is not a deny-list.
/// </remarks>
public interface IAccessTokenAudit
{
    /// <summary>Records an issuance.</summary>
    /// <param name="capability">The capability as minted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row as recorded.</returns>
    ValueTask<IssuedCapability> RecordAsync(
        IssuedCapability capability,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one row.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="tokenId">The capability's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or <see langword="null"/> when it was never issued.</returns>
    ValueTask<IssuedCapability?> GetAsync(
        string tenant,
        string tokenId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists a tenant's rows, newest first.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows.</returns>
    ValueTask<IReadOnlyList<IssuedCapability>> ListAsync(
        string tenant,
        CancellationToken cancellationToken = default);

    /// <summary>Withdraws a capability.</summary>
    /// <remarks>
    /// Idempotent: withdrawing one that is already withdrawn answers with the row and the instant it first happened,
    /// because an operator acting on a stale list should not be told they did something twice.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="tokenId">The capability's identity.</param>
    /// <param name="revokedAt">When it is withdrawn, in seconds since the epoch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row as it now stands, or <see langword="null"/> when it was never issued.</returns>
    ValueTask<IssuedCapability?> RevokeAsync(
        string tenant,
        string tokenId,
        long revokedAt,
        CancellationToken cancellationToken = default);
}
