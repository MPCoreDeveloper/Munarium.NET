namespace Munarium.Access;

using Munarium.Evidence;

/// <summary>The scopes a capability can carry.</summary>
/// <remarks>
/// The original names four and this port one more, and the distinctions are the point rather than the names: a reconciliation service must not be
/// able to upload documents, and an uploader must not be able to write governance findings. A scope says which plane a
/// capability participates in; level and compartments still say how far it reaches within it.
/// </remarks>
public static class AccessScope
{
    /// <summary>The session and turn data plane.</summary>
    public const string Query = "query";

    /// <summary>The file-ingestion plane.</summary>
    public const string Ingest = "ingest";

    /// <summary>Filing findings, which is a service's privilege and never a gate's.</summary>
    public const string Findings = "findings";

    /// <summary>Sealing and resolving evidence artifacts.</summary>
    /// <remarks>
    /// Worth stating what this does not do, because it is the kind of thing a reader assumes the other way round: it never
    /// widens what a capability may read. Domination is still checked per artifact, so an <c>evidence</c>-scoped token
    /// under-cleared for an artifact is refused exactly as any other principal is.
    /// </remarks>
    public const string Evidence = "evidence";

    /// <summary>Reading the issuance audit and withdrawing a capability.</summary>
    /// <remarks>
    /// This port's own, and the one scope the original has no name for: it names four, and the surfaces that read the audit
    /// and end a capability are additions here. It is a scope of its own rather than a corner of a wider one, because
    /// seeing which credentials exist and withdrawing one is administrative work - not reading governance's findings, and
    /// above all not uploading documents.
    /// </remarks>
    public const string Access = "access";
}

/// <summary>The claim set a capability token carries.</summary>
/// <remarks>
/// The field names are the wire contract and are deliberately terse: they ride every data-plane request. This port maps
/// them by hand like every other codec here, so nothing about a token depends on reflection.
/// </remarks>
public sealed record AccessClaims
{
    /// <summary>Gets the end-user identity the API manager asserted.</summary>
    public required string Subject { get; init; }

    /// <summary>Gets the tenant the capability is scoped to.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the hierarchical access level; a collection at level L needs at least L.</summary>
    public required int Level { get; init; }

    /// <summary>Gets the need-to-know compartments.</summary>
    public IReadOnlyList<string> Compartments { get; init; } = [];

    /// <summary>Gets the capabilities carried.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Gets the runbook names permitted, or <see langword="null"/> for any the level permits.</summary>
    public IReadOnlyList<string>? Runbooks { get; init; }

    /// <summary>Gets the capability's own identifier, which is what a revocation names.</summary>
    /// <remarks>
    /// A token is a bearer credential and cannot be recalled once it is out, so the only way to withdraw one is to record
    /// its identity and refuse it at verification. That is why the identifier is a claim and not a database key.
    /// </remarks>
    public required string TokenId { get; init; }

    /// <summary>Gets when the capability was issued, in seconds since the epoch.</summary>
    public required long IssuedAt { get; init; }

    /// <summary>Gets when it stops being valid, in seconds since the epoch.</summary>
    public required long ExpiresAt { get; init; }

    /// <summary>Reports whether the capability carries a scope.</summary>
    /// <param name="scope">The scope, as <see cref="AccessScope"/> names it.</param>
    /// <returns>Whether it is carried.</returns>
    public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);

    /// <summary>Reads the capability as the context the kernel's gates ask about.</summary>
    /// <returns>The context.</returns>
    public AccessContext ToContext() => new(Level, Compartments, Runbooks: Runbooks);

    /// <summary>Reads the capability as the principal a plane resolves as.</summary>
    /// <remarks>
    /// A capability is never an unrestricted principal: <see cref="EvidencePrincipal.ForDeployment"/> is what a deployment
    /// without authorization hands to everybody, and the whole point of a token is that it says something narrower. A
    /// capability that clears every compartment says so one compartment at a time, which is a list and not a flag.
    /// </remarks>
    /// <returns>The principal.</returns>
    public EvidencePrincipal ToPrincipal() => new()
    {
        Tenant = Tenant,
        Uid = Subject,
        Level = Level,
        Compartments = Compartments,
    };
}

/// <summary>Why a capability was refused.</summary>
/// <param name="Reason">What was wrong with it.</param>
public sealed record AccessRefused(string Reason);

/// <summary>The result of verifying a capability: its claims, or why they were refused.</summary>
public readonly union AccessOutcome(AccessClaims, AccessRefused);
