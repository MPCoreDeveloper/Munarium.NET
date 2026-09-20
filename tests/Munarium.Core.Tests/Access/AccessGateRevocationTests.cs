namespace Munarium.Core.Tests.Access;

using System.Text;
using Munarium.Access;
using Munarium.Core.Tests.Support;
using Munarium.Evidence;

/// <summary>
/// Tests for withdrawal: the one thing that can reach a capability after it was handed out.
/// </summary>
/// <remarks>
/// A bearer credential cannot be recalled and nothing here stores token material, so the row is the lever - and these
/// tests are about that lever being pulled, and pulled narrowly.
/// </remarks>
public class AccessGateRevocationTests
{
    private const string Subject = "tyler@example.com";
    private const string Tenant = "default";

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("a-test-secret-long-enough-to-sign-with");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A withdrawn capability is refused, and the refusal says so.</summary>
    [Fact]
    public async Task AWithdrawnCapabilityIsRefused()
    {
        var audit = new InMemoryAccessTokenAudit();

        await audit.RecordAsync(Row("jti-withdrawn"));
        await audit.RevokeAsync(Tenant, "jti-withdrawn", Now.ToUnixTimeSeconds());

        var refused = await Refusal(audit, TokenFor("jti-withdrawn"));

        Assert.Contains("withdrawn", refused.Reason, StringComparison.Ordinal);
    }

    /// <summary>A capability that was issued and stands is served as its own principal.</summary>
    [Fact]
    public async Task ACapabilityThatStandsIsServed()
    {
        var audit = new InMemoryAccessTokenAudit();

        await audit.RecordAsync(Row("jti-standing"));

        var access = await Gate(audit).ResolveAsync($"Bearer {TokenFor("jti-standing")}", AccessScope.Query, Now);

        var principal = await Principal(audit, TokenFor("jti-standing"));

        Assert.Equal(Subject, principal.Uid);
        Assert.Equal(Tenant, principal.Tenant);
        Assert.Equal(3, principal.Level);
        Assert.Equal(["alpha"], principal.Compartments);
    }

    /// <summary>
    /// Withdrawal names one capability, not a person: another one issued to the same subject still serves.
    /// </summary>
    /// <remarks>
    /// This is the difference between a deny-list and an account suspension, and it is why the identity is a claim rather
    /// than a database key: two capabilities of one subject are two rows and two decisions.
    /// </remarks>
    [Fact]
    public async Task WithdrawingOneCapabilityLeavesAnotherOfTheSameSubjectStanding()
    {
        var audit = new InMemoryAccessTokenAudit();

        await audit.RecordAsync(Row("jti-first"));
        await audit.RecordAsync(Row("jti-second"));
        await audit.RevokeAsync(Tenant, "jti-first", Now.ToUnixTimeSeconds());

        var gate = Gate(audit);
        var withdrawn = await gate.ResolveAsync($"Bearer {TokenFor("jti-first")}", AccessScope.Query, Now);
        var standing = await gate.ResolveAsync($"Bearer {TokenFor("jti-second")}", AccessScope.Query, Now);

        Assert.True(withdrawn is AccessRefused, $"the withdrawn capability was served: {withdrawn}");
        Assert.True(standing is EvidencePrincipal, $"the standing capability was refused: {standing}");
    }

    /// <summary>A capability the audit never saw stands.</summary>
    /// <remarks>
    /// Deliberately the opposite of a safelist, and stated because it is the kind of choice a reader assumes the other way
    /// round: this is a deny-list. Refusing on absence would turn a missing row - a store that failed to save, or a token
    /// minted by a neighbouring service that shares the secret - into an outage for a capability nobody withdrew.
    /// </remarks>
    [Fact]
    public async Task ACapabilityThatWasNeverRecordedStands()
    {
        var access = await Gate(new InMemoryAccessTokenAudit())
            .ResolveAsync($"Bearer {TokenFor("jti-unrecorded")}", AccessScope.Query, Now);

        Assert.True(access is EvidencePrincipal, $"expected a principal, got {access}");
    }

    /// <summary>An expired capability is refused for being expired, whether or not it was also withdrawn.</summary>
    /// <remarks>
    /// The order is the point: verification settles expiry before the audit is consulted, so a withdrawal is never the
    /// reason reported for a token that was already dead when it arrived.
    /// </remarks>
    [Fact]
    public async Task ExpiryIsReportedBeforeWithdrawal()
    {
        var audit = new InMemoryAccessTokenAudit();
        var expired = AccessTokens.Mint(
            Secret,
            new AccessClaims
            {
                Subject = Subject,
                Tenant = Tenant,
                Level = 3,
                Compartments = ["alpha"],
                Scopes = [AccessScope.Query],
                TokenId = "jti-expired",
                IssuedAt = 0,
                ExpiresAt = Now.AddHours(-2).ToUnixTimeSeconds(),
            });

        await audit.RecordAsync(Row("jti-expired"));
        await audit.RevokeAsync(Tenant, "jti-expired", Now.ToUnixTimeSeconds());

        var refused = await Refusal(audit, expired);

        Assert.Contains("expired", refused.Reason, StringComparison.Ordinal);
    }

    /// <summary>Gets a gate that requires a capability and consults an audit.</summary>
    /// <param name="audit">The audit to consult.</param>
    /// <returns>The gate.</returns>
    private static AccessGate Gate(IAccessTokenAudit audit) =>
        new(Secret, authorized: true, EvidencePrincipal.ForDeployment(Tenant), audit);

    /// <summary>Resolves a token and insists the answer is a refusal.</summary>
    /// <param name="audit">The audit to consult.</param>
    /// <param name="token">The capability.</param>
    /// <returns>The refusal.</returns>
    private static async Task<AccessRefused> Refusal(IAccessTokenAudit audit, string token)
    {
        var access = await Gate(audit).ResolveAsync($"Bearer {token}", AccessScope.Query, Now);

        Assert.True(access is AccessRefused, $"expected a refusal, got {access}");

        return access is AccessRefused refused ? refused : new AccessRefused("unreachable: the assertion above threw");
    }

    /// <summary>Resolves a token and insists the answer is a principal.</summary>
    /// <param name="audit">The audit to consult.</param>
    /// <param name="token">The capability.</param>
    /// <returns>The principal.</returns>
    private static async Task<EvidencePrincipal> Principal(IAccessTokenAudit audit, string token)
    {
        var access = await Gate(audit).ResolveAsync($"Bearer {token}", AccessScope.Query, Now);

        Assert.True(access is EvidencePrincipal, $"expected a principal, got {access}");

        return access is EvidencePrincipal served ? served : EvidencePrincipal.ForDeployment(Tenant);
    }

    /// <summary>Mints a capability that expires an hour after the tests' clock.</summary>
    /// <param name="tokenId">Its identity.</param>
    /// <returns>The token.</returns>
    private static string TokenFor(string tokenId) => AccessTokens.Mint(
        Secret,
        AccessTokens.Issue(
            new AccessClaims
            {
                Subject = Subject,
                Tenant = Tenant,
                Level = 3,
                Compartments = ["alpha"],
                Scopes = [AccessScope.Query],
                TokenId = tokenId,
                IssuedAt = 0,
                ExpiresAt = 0,
            },
            Now,
            TimeSpan.FromHours(1)));

    /// <summary>The row an issuance would leave behind.</summary>
    /// <param name="tokenId">The capability's identity.</param>
    /// <returns>The row.</returns>
    private static IssuedCapability Row(string tokenId) => new()
    {
        TokenId = tokenId,
        Tenant = Tenant,
        Subject = Subject,
        Level = 3,
        Compartments = ["alpha"],
        Scopes = [AccessScope.Query],
        IssuedAt = Now.ToUnixTimeSeconds(),
        ExpiresAt = Now.AddHours(1).ToUnixTimeSeconds(),
    };
}
