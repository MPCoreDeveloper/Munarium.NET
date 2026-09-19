namespace Munarium.Access;

using Munarium.Evidence;

/// <summary>The result of resolving a request: who is asking, or why nobody is.</summary>
public readonly union AccessResolution(EvidencePrincipal, AccessRefused);

/// <summary>Reads a capability into a principal, enforcing the scope a plane requires.</summary>
/// <remarks>
/// This is the one place a presented capability becomes an identity, so the rules are here rather than in each route:
/// <list type="bullet">
/// <item><description>
/// A request with no capability is the deployment principal when the deployment has no authorization configured, and a
/// refusal when it does - the original requires a uid on every request in that mode, and a call nobody authorized must
/// not become one.
/// </description></item>
/// <item><description>
/// A capability that does not verify is refused, whatever it claims. The signature is checked before the claims are read,
/// so nothing here trusts a field that was not signed.
/// </description></item>
/// <item><description>
/// A capability that verifies but does not carry the plane's scope is refused. The scope says which plane a caller
/// participates in; level and compartments then say how far it reaches inside that plane, and neither substitutes for the
/// other.
/// </description></item>
/// </list>
/// </remarks>
/// <param name="secret">The deployment secret capabilities are signed with.</param>
/// <param name="authorized">Whether this deployment requires a capability at all.</param>
/// <param name="fallback">The principal a deployment without authorization maps every caller to.</param>
public sealed class AccessGate(
    ReadOnlyMemory<byte> secret,
    bool authorized,
    EvidencePrincipal fallback)
{
    /// <summary>The refusal a plane answers with when a capability is required and none was presented.</summary>
    public const string MissingReason =
        "a capability is required on this request: present one as an Authorization bearer token";

    /// <summary>Gets the deployment secret.</summary>
    public ReadOnlyMemory<byte> Secret { get; } = secret;

    /// <summary>Gets a value indicating whether this deployment requires a capability.</summary>
    public bool Authorized { get; } = authorized;

    /// <summary>Gets the principal a deployment without authorization maps every caller to.</summary>
    public EvidencePrincipal Fallback { get; } = fallback;

    /// <summary>Resolves a request's principal, requiring one scope.</summary>
    /// <param name="authorization">The request's Authorization header, or <see langword="null"/>.</param>
    /// <param name="scope">The scope the plane requires.</param>
    /// <param name="now">The instant to judge expiry at.</param>
    /// <returns>The principal, or why the request has none.</returns>
    public AccessResolution Resolve(string? authorization, string scope, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var token = Bearer(authorization);

        if (token is null)
        {
            return Authorized
                ? new AccessRefused(MissingReason)
                : Fallback;
        }

        var verified = AccessTokens.Verify(Secret.Span, token, now);

        return verified switch
        {
            AccessClaims claims when claims.HasScope(scope) => claims.ToPrincipal(),
            AccessClaims => new AccessRefused($"this capability does not carry the {scope} scope"),
            AccessRefused refused => refused,
        };
    }

    /// <summary>Reads the token out of an Authorization header.</summary>
    /// <param name="authorization">The header, or <see langword="null"/>.</param>
    /// <returns>The token, or <see langword="null"/> when the header carries none.</returns>
    private static string? Bearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return null;
        }

        var header = authorization.Trim();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7
            ? header[7..].Trim()
            : null;
    }
}
