namespace Munarium.Core.Tests.Access;

using System.Text;
using System.Text.Json;
using Munarium.Access;

/// <summary>
/// Tests for the capability layer: what a token carries, what the kernel reads out of it, and every way one is refused.
/// </summary>
/// <remarks>
/// The secret is a literal here, because a test that read one from configuration would be testing the configuration.
/// </remarks>
public class AccessTokenTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("a-test-secret-long-enough-to-sign-with");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A capability round-trips, claims included.</summary>
    [Fact]
    public void ACapabilityRoundTripsWithItsClaims()
    {
        var issued = AccessTokens.Issue(Claims(), Now);
        var token = AccessTokens.Mint(Secret, issued);

        var verified = Verified(AccessTokens.Verify(Secret, token, Now));

        Assert.Equal("tyler@example.com", verified.Subject);
        Assert.Equal("acme", verified.Tenant);
        Assert.Equal(3, verified.Level);
        Assert.Equal(["north"], verified.Compartments);
        Assert.Equal(["query"], verified.Scopes);
        Assert.Equal(["vendor-memory"], verified.Runbooks);
        Assert.Equal(issued.IssuedAt, verified.IssuedAt);
        Assert.Equal(issued.ExpiresAt, verified.ExpiresAt);
    }

    /// <summary>
    /// The claim names are the wire contract, which is why they are terse - and why they are asserted rather than assumed.
    /// </summary>
    [Fact]
    public void TheClaimNamesAreTheContracts()
    {
        var payload = AccessTokens.Mint(Secret, AccessTokens.Issue(Claims(), Now)).Split('.')[1];

        var padded = payload.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => string.Concat(padded, "=="),
            3 => string.Concat(padded, "="),
            _ => padded,
        };

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));

        // The original's names, verbatim: a shorter claim set is not compatible with a client that reads these.
        foreach (var name in new[] { "sub", "ten", "jti", "lvl", "cmp", "scopes", "iat", "exp" })
        {
            Assert.True(document.RootElement.TryGetProperty(name, out _), $"a capability must carry '{name}'");
        }
    }

    /// <summary>A capability reads as the context the kernel's gates ask about, with both gates intact.</summary>
    [Fact]
    public void ACapabilityReadsAsTheContextItsGatesAsk()
    {
        var context = Verified(AccessTokens.Verify(
            Secret,
            AccessTokens.Mint(Secret, AccessTokens.Issue(Claims(), Now)),
            Now)).ToContext();

        Assert.True(context.Permits(3, ["north"]));
        Assert.True(context.Permits(2, ["north"]));

        // Neither gate substitutes for the other: too low a level fails, and so does a compartment it does not hold.
        Assert.False(context.Permits(4, ["north"]));
        Assert.False(context.Permits(3, ["north", "south"]));

        // An allowlist is an allowlist, and a name outside it is not permitted.
        Assert.True(context.PermitsRunbook("vendor-memory"));
        Assert.False(context.PermitsRunbook("somebody-elses"));
    }

    /// <summary>Scopes are carried, and a scope that was not granted is not held.</summary>
    [Fact]
    public void ScopesAreCarriedVerbatim()
    {
        var verified = Verified(AccessTokens.Verify(
            Secret,
            AccessTokens.Mint(Secret, AccessTokens.Issue(Claims(), Now)),
            Now));

        Assert.True(verified.HasScope(AccessScope.Query));
        Assert.False(verified.HasScope(AccessScope.Ingest));
        Assert.False(verified.HasScope(AccessScope.Findings));
        Assert.False(verified.HasScope(AccessScope.Evidence));
    }

    /// <summary>A capability signed with another deployment's secret is refused, and so is a tampered one.</summary>
    [Fact]
    public void AnotherSecretAndATamperedCapabilityAreBothRefused()
    {
        var token = AccessTokens.Mint(Secret, AccessTokens.Issue(Claims(), Now));
        var parts = token.Split('.');
        var other = AccessTokens.Mint(Encoding.UTF8.GetBytes("a-different-secret"), AccessTokens.Issue(Claims(), Now));

        Refused(AccessTokens.Verify(Secret, other, Now));

        // The claims are replaced under the same signature, which is exactly what signing is for: it must not verify.
        var forged = string.Concat(
            parts[0], ".", parts[1].TrimEnd(parts[1][^1]), "X", ".", parts[2]);

        Refused(AccessTokens.Verify(Secret, forged, Now));
    }

    /// <summary>An expired capability is refused, and the allowance is the original's thirty seconds.</summary>
    [Fact]
    public void ExpiryIsRefusedAndTheLeewayIsHonoured()
    {
        var token = AccessTokens.Mint(Secret, AccessTokens.Issue(Claims(), Now, TimeSpan.FromMinutes(1)));

        _ = Verified(AccessTokens.Verify(Secret, token, Now.AddSeconds(59)));
        _ = Verified(AccessTokens.Verify(Secret, token, Now.AddSeconds(89)));
        Refused(AccessTokens.Verify(Secret, token, Now.AddSeconds(91)));
    }

    /// <summary>Issuance clamps a lifetime past the ceiling rather than honouring it.</summary>
    [Fact]
    public void AnOverlongLifetimeIsClamped()
    {
        var issued = AccessTokens.Issue(Claims(), Now, TimeSpan.FromDays(365));

        Assert.Equal(AccessTokens.MaxLifetime.TotalSeconds, issued.ExpiresAt - issued.IssuedAt);
    }

    /// <summary>The default lifetime is the original's hour, and a lifetime that is not positive is refused.</summary>
    [Fact]
    public void TheDefaultLifetimeIsAnHourAndANonPositiveOneIsRefused()
    {
        var issued = AccessTokens.Issue(Claims(), Now);

        Assert.Equal(AccessTokens.DefaultLifetime.TotalSeconds, issued.ExpiresAt - issued.IssuedAt);
        Assert.Throws<ArgumentOutOfRangeException>(() => AccessTokens.Issue(Claims(), Now, TimeSpan.Zero));
    }

    /// <summary>A capability with no allowlist permits any runbook its level allows.</summary>
    [Fact]
    public void ACapabilityWithoutAnAllowlistPermitsAnyRunbook()
    {
        var claims = AccessTokens.Issue(Claims() with { Runbooks = null }, Now);
        var context = Verified(AccessTokens.Verify(Secret, AccessTokens.Mint(Secret, claims), Now)).ToContext();

        Assert.True(context.PermitsRunbook("any-name-at-all"));
    }

    /// <summary>What is not a capability is refused by name rather than throwing.</summary>
    /// <param name="token">What was presented.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-capability")]
    [InlineData("two.parts")]
    [InlineData("three.parts.here")]
    public void SomethingThatIsNotACapabilityIsRefused(string? token) =>
        Refused(AccessTokens.Verify(Secret, token, Now));

    /// <summary>Builds the claims a test issues.</summary>
    /// <returns>The claims; the instants are filled in by issuance.</returns>
    private static AccessClaims Claims() => new()
    {
        Subject = "tyler@example.com",
        TokenId = "01J000000000000000000000AA",
        Tenant = "acme",
        Level = 3,
        Compartments = ["north"],
        Scopes = [AccessScope.Query],
        Runbooks = ["vendor-memory"],
        IssuedAt = 0,
        ExpiresAt = 0,
    };

    /// <summary>Reads the claims out of a verified outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The claims.</returns>
    private static AccessClaims Verified(AccessOutcome outcome) => outcome is AccessClaims claims
        ? claims
        : throw new InvalidOperationException($"the capability was refused: {outcome}");

    /// <summary>Reads the refusal out of a refused outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The refusal.</returns>
    private static AccessRefused Refused(AccessOutcome outcome) => outcome is AccessRefused refused
        ? refused
        : throw new InvalidOperationException($"the capability was accepted: {outcome}");
}
