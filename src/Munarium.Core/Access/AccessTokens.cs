namespace Munarium.Access;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Mints and verifies capability tokens.</summary>
/// <remarks>
/// HS256 over one server-held secret, verified locally: no key set to fetch, no provider to call, nothing to introspect.
/// That is the original's decision, and it suits this port for the same reason: a JWT is three base64url segments and an
/// HMAC, all of it in the base class library, so a token layer costs no dependency and stays AOT-clean.
/// <para>
/// A token is not identity. An identity provider in front authenticates people and exchanges its own long-lived credential
/// for one of these, which is short-lived and least-privilege; this type is the mechanics of that exchange, not a place
/// where anybody is authenticated.
/// </para>
/// </remarks>
public static class AccessTokens
{
    /// <summary>The hard ceiling on a lifetime, which issuance clamps to.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(86_400);

    /// <summary>The lifetime a deployment gets when it does not choose one.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(3_600);

    /// <summary>The clock-skew allowance applied to expiry.</summary>
    public static readonly TimeSpan Leeway = TimeSpan.FromSeconds(30);

    /// <summary>Issues a capability, clamping the lifetime to the ceiling and refusing what cannot be issued.</summary>
    /// <remarks>
    /// The refusals are the original's and each of them is a rule rather than a formality: an unnamed subject cannot be
    /// audited, a capability with no scope can do nothing and would be issued only by mistake, and a scope nobody defined
    /// is a typo that would otherwise sit in a token looking like authority.
    /// </remarks>
    /// <param name="claims">The claims to carry.</param>
    /// <param name="issuedAt">When it is issued.</param>
    /// <param name="lifetime">How long it should live, or <see langword="null"/> for the default.</param>
    /// <returns>The claims as they will be signed.</returns>
    /// <exception cref="ArgumentException">Thrown when the subject, the scopes or a scope name is not issuable.</exception>
    public static AccessClaims Issue(AccessClaims claims, DateTimeOffset issuedAt, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(claims);

        if (string.IsNullOrWhiteSpace(claims.Subject))
        {
            throw new ArgumentException("A capability names its subject: an unnamed one cannot be audited.", nameof(claims));
        }

        if (claims.Scopes.Count == 0)
        {
            throw new ArgumentException(
                "A capability carries at least one scope (query|ingest|findings|evidence|access).",
                nameof(claims));
        }

        if (claims.Scopes.Any(scope => scope is not (AccessScope.Query or AccessScope.Ingest
            or AccessScope.Findings or AccessScope.Evidence or AccessScope.Access)))
        {
            throw new ArgumentException(
                $"A scope is one of query|ingest|findings|evidence|access: '{string.Join(", ", claims.Scopes)}' is not.",
                nameof(claims));
        }

        var ttl = lifetime ?? DefaultLifetime;

        // Longer than the ceiling is clamped rather than refused: the caller asked for the most it can have, and a token
        // that outlives a secret rotation is a worse failure than a shorter one.
        var clamped = ttl > MaxLifetime ? MaxLifetime : ttl;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(clamped, TimeSpan.Zero);

        return claims with
        {
            IssuedAt = issuedAt.ToUnixTimeSeconds(),
            ExpiresAt = issuedAt.Add(clamped).ToUnixTimeSeconds(),
        };
    }

    /// <summary>Gets the secret a deployment signs with, or a generated one when it set none.</summary>
    /// <remarks>
    /// A configured secret is what makes a capability survive a restart; without one every token is invalid the moment
    /// the process ends, which is the right behaviour for a development run and the wrong one for a deployment.
    /// </remarks>
    /// <returns>The secret.</returns>
    public static byte[] Secret() =>
        Environment.GetEnvironmentVariable("MUNARIUM_ACCESS_SECRET") is { Length: > 0 } configured
            ? Encoding.UTF8.GetBytes(configured)
            : RandomNumberGenerator.GetBytes(32);

    /// <summary>Signs claims into a token.</summary>
    /// <param name="secret">The server-held secret.</param>
    /// <param name="claims">The claims, already issued.</param>
    /// <returns>The token.</returns>
    public static string Mint(ReadOnlySpan<byte> secret, AccessClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payload = Base64Url(Payload(claims));
        var signing = string.Concat(header, ".", payload);

        return string.Concat(signing, ".", Base64Url(Sign(secret, signing)));
    }

    /// <summary>Verifies a token and reads its claims.</summary>
    /// <param name="secret">The server-held secret.</param>
    /// <param name="token">The token.</param>
    /// <param name="now">The instant to judge expiry at, so no caller has to read a clock it cannot place.</param>
    /// <returns>The claims, or why the token was refused.</returns>
    public static AccessOutcome Verify(ReadOnlySpan<byte> secret, string? token, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AccessRefused("no capability was presented");
        }

        var parts = token.Split('.');

        if (parts.Length != 3)
        {
            return new AccessRefused("a capability is three segments separated by dots");
        }

        var signing = string.Concat(parts[0], ".", parts[1]);

        // The signature is checked before the claims are read: a claim from an unsigned token is a claim anybody could have
        // written, and parsing first would be trusting the input to decide whether to trust the input.
        byte[] signature;

        try
        {
            signature = UnBase64Url(parts[2]);
        }
        catch (FormatException)
        {
            // A signature that is not base64url is not a signature, and a caller-supplied token must never throw: it is
            // refused like everything else that is not a capability.
            return new AccessRefused("the capability signature is not base64url");
        }

        if (!CryptographicOperations.FixedTimeEquals(Sign(secret, signing), signature))
        {
            return new AccessRefused("the capability was not signed with this deployment's secret");
        }

        AccessClaims claims;

        try
        {
            claims = Read(UnBase64Url(parts[1]));
        }
        catch (Exception failure) when (failure is JsonException or FormatException or KeyNotFoundException
            or InvalidOperationException or ArgumentException)
        {
            return new AccessRefused($"the capability's claims could not be read: {failure.Message}");
        }

        return claims.ExpiresAt + (long)Leeway.TotalSeconds < now.ToUnixTimeSeconds()
            ? new AccessRefused("the capability has expired")
            : claims;
    }

    /// <summary>Signs a string with the deployment secret.</summary>
    /// <param name="secret">The secret.</param>
    /// <param name="value">What to sign.</param>
    /// <returns>The signature.</returns>
    private static byte[] Sign(ReadOnlySpan<byte> secret, string value) =>
        HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(value));

    /// <summary>Writes the claims as the payload, by hand like every other codec here.</summary>
    /// <param name="claims">The claims.</param>
    /// <returns>The payload bytes.</returns>
    private static byte[] Payload(AccessClaims claims)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sub", claims.Subject);
            writer.WriteString("ten", claims.Tenant);
            writer.WriteString("jti", claims.TokenId);
            writer.WriteNumber("lvl", claims.Level);
            Strings(writer, "cmp", claims.Compartments);
            Strings(writer, "scopes", claims.Scopes);

            if (claims.Runbooks is { } runbooks)
            {
                Strings(writer, "runbooks", runbooks);
            }

            writer.WriteNumber("iat", claims.IssuedAt);
            writer.WriteNumber("exp", claims.ExpiresAt);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Reads claims out of a payload.</summary>
    /// <param name="payload">The payload bytes.</param>
    /// <returns>The claims.</returns>
    private static AccessClaims Read(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        return new AccessClaims
        {
            Subject = root.GetProperty("sub").GetString() ?? string.Empty,
            Tenant = root.GetProperty("ten").GetString() ?? string.Empty,
            TokenId = root.GetProperty("jti").GetString() ?? string.Empty,
            Level = root.GetProperty("lvl").GetInt32(),
            Compartments = Texts(root, "cmp"),
            Scopes = Texts(root, "scopes"),
            Runbooks = root.TryGetProperty("runbooks", out _) ? Texts(root, "runbooks") : null,
            IssuedAt = root.GetProperty("iat").GetInt64(),
            ExpiresAt = root.GetProperty("exp").GetInt64(),
        };
    }

    private static void Strings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);

        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static IReadOnlyList<string> Texts(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind is JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(item => item.GetString() ?? string.Empty)]
            : [];

    /// <summary>Encodes base64url without padding, which is what a JWT segment is.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The segment.</returns>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes a base64url segment.</summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The bytes.</returns>
    private static byte[] UnBase64Url(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');

        // A segment carries no padding, and how much it needs depends on what was encoded, so it is put back rather than
        // assumed absent.
        padded = (padded.Length % 4) switch
        {
            2 => string.Concat(padded, "=="),
            3 => string.Concat(padded, "="),
            _ => padded,
        };

        return Convert.FromBase64String(padded);
    }
}
