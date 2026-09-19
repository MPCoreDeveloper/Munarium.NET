namespace Munarium.Core.Tests.Access;

using System.Text;
using Munarium.Access;
using Munarium.Evidence;

/// <summary>Tests for the one place a presented capability becomes an identity.</summary>
public class AccessGateTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("a-test-secret-long-enough-to-sign-with");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A deployment without authorization maps every caller to its own principal.</summary>
    [Fact]
    public void ADeploymentWithoutAuthorizationFallsBackToItsPrincipal()
    {
        var gate = new AccessGate(Secret, authorized: false, EvidencePrincipal.ForDeployment("acme"));

        var granted = Granted(gate.Resolve(null, AccessScope.Query, Now));

        Assert.Equal("acme", granted.Tenant);
        Assert.True(granted.AllCompartments);
    }

    /// <summary>A deployment with authorization refuses a request that presents nothing.</summary>
    [Fact]
    public void ADeploymentWithAuthorizationRefusesACallWithoutACapability()
    {
        var gate = new AccessGate(Secret, authorized: true, EvidencePrincipal.ForDeployment("acme"));

        var refused = Refused(gate.Resolve(null, AccessScope.Query, Now));

        Assert.Equal(AccessGate.MissingReason, refused.Reason);
    }

    /// <summary>A capability carrying the plane's scope becomes that caller's principal.</summary>
    [Fact]
    public void ACapabilityWithTheScopeBecomesThePrincipal()
    {
        var gate = new AccessGate(Secret, authorized: true, EvidencePrincipal.ForDeployment("acme"));

        var granted = Granted(gate.Resolve(
            "Bearer " + Token("tyler@example.com", 3, ["north"], [AccessScope.Query]),
            AccessScope.Query,
            Now));

        Assert.Equal("tyler@example.com", granted.Uid);
        Assert.Equal(3, granted.Level);
        Assert.Equal(["north"], granted.Compartments);

        // Never the deployment principal: the point of a capability is that it says something narrower.
        Assert.False(granted.AllCompartments);
    }

    /// <summary>A capability that verifies but belongs to another plane is refused, which is what a scope is for.</summary>
    [Fact]
    public void ACapabilityWithoutTheScopeIsRefused()
    {
        var gate = new AccessGate(Secret, authorized: true, EvidencePrincipal.ForDeployment("acme"));
        var ingestOnly = "Bearer " + Token("uploader", 9, [], [AccessScope.Ingest]);

        Refused(gate.Resolve(ingestOnly, AccessScope.Query, Now));
        Granted(gate.Resolve(ingestOnly, AccessScope.Ingest, Now));
    }

    /// <summary>What does not verify is refused, whatever it claims - and the header itself is read strictly.</summary>
    /// <param name="header">The header presented.</param>
    [Theory]
    [InlineData("Bearer not-a-capability")]
    [InlineData("Bearer a.b.c")]
    [InlineData("Basic dHlsZXI6c2VjcmV0")]
    [InlineData("Bearer ")]
    public void WhatDoesNotPresentACapabilityIsRefused(string header)
    {
        var gate = new AccessGate(Secret, authorized: true, EvidencePrincipal.ForDeployment("acme"));

        Refused(gate.Resolve(header, AccessScope.Query, Now));
    }

    /// <summary>An expired capability is refused at the gate, and the leeway is the original's.</summary>
    [Fact]
    public void AnExpiredCapabilityIsRefused()
    {
        var gate = new AccessGate(Secret, authorized: true, EvidencePrincipal.ForDeployment("acme"));
        var claims = AccessTokens.Issue(
            new AccessClaims
            {
                Subject = "tyler@example.com",
                Tenant = "acme",
                Level = 1,
                TokenId = "01J000000000000000000000AA",
                Scopes = [AccessScope.Query],
                IssuedAt = 0,
                ExpiresAt = 0,
            },
            Now,
            TimeSpan.FromMinutes(1));

        var token = AccessTokens.Mint(Secret, claims);

        Granted(gate.Resolve("Bearer " + token, AccessScope.Query, Now.AddSeconds(89)));
        Refused(gate.Resolve("Bearer " + token, AccessScope.Query, Now.AddSeconds(91)));
    }

    /// <summary>Mints a capability for a test.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="level">The level.</param>
    /// <param name="compartments">The compartments.</param>
    /// <param name="scopes">The scopes.</param>
    /// <returns>The token.</returns>
    private static string Token(string subject, int level, IReadOnlyList<string> compartments, IReadOnlyList<string> scopes) =>
        AccessTokens.Mint(
            Secret,
            AccessTokens.Issue(
                new AccessClaims
                {
                    Subject = subject,
                    Tenant = "acme",
                    Level = level,
                    Compartments = compartments,
                    Scopes = scopes,
                    TokenId = "01J000000000000000000000AA",
                    IssuedAt = 0,
                    ExpiresAt = 0,
                },
                Now));

    /// <summary>Reads the principal out of a granted outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The principal.</returns>
    private static EvidencePrincipal Granted(AccessResolution outcome) => outcome is EvidencePrincipal principal
        ? principal
        : throw new InvalidOperationException($"the request was refused: {outcome}");

    /// <summary>Reads the refusal out of a refused outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The refusal.</returns>
    private static AccessRefused Refused(AccessResolution outcome) => outcome is AccessRefused refused
        ? refused
        : throw new InvalidOperationException($"the request was granted: {outcome}");
}
