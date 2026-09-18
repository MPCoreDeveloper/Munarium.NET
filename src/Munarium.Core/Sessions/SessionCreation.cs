namespace Munarium.Sessions;

using Munarium.Access;
using Munarium.Runbooks;

/// <summary>
/// The codes a session creation can refuse with.
/// </summary>
/// <remarks>
/// Kebab-case, like the evidence plane's refusals and the server's problem registry: a refusal is a thing a caller
/// sees, so its code is part of the contract rather than a log line.
/// </remarks>
public static class SessionRefusalCodes
{
    /// <summary>The clearance does not cover the runbook.</summary>
    public const string RunbookNotPermitted = "runbook-not-permitted";

    /// <summary>The clearance covers the runbook but none of its collections.</summary>
    public const string NoVisibleCollections = "no-visible-collections";

    /// <summary>The session named by a turn does not exist.</summary>
    public const string SessionNotFound = "session-not-found";

    /// <summary>The session exists and is no longer accepting turns.</summary>
    public const string SessionClosed = "session-closed";
}

/// <summary>A creation that was refused, and why.</summary>
/// <param name="Code">The kebab-case code, from <see cref="SessionRefusalCodes"/>.</param>
/// <param name="Message">The message, which has to be safe to show a caller.</param>
public sealed record SessionRefusal(string Code, string Message);

/// <summary>A session that was opened, with what its clearance covers.</summary>
public sealed record SessionOpened
{
    /// <summary>Gets the session as recorded.</summary>
    public required SessionRecord Session { get; init; }

    /// <summary>
    /// Gets the collections the clearance permits, in the document's order.
    /// </summary>
    /// <remarks>
    /// The least-privilege echo, so a client knows what it can see before asking anything, and the same list the
    /// creation's own rule is measured by: an empty one is a refusal rather than an empty session.
    /// </remarks>
    public required IReadOnlyList<string> PermittedCollections { get; init; }
}

/// <summary>
/// The result of opening a session.
/// </summary>
public readonly union SessionOpening(SessionOpened, SessionRefusal);

/// <summary>
/// Opening a session: the rules, in one place.
/// </summary>
/// <remarks>
/// The rules are here rather than in the transport because both transports have to refuse identically - a caller that
/// gets a different answer over gRPC than over HTTP has been told two different things about its own clearance.
/// </remarks>
public static class SessionCreation
{
    /// <summary>
    /// Opens a session over a runbook, if the clearance allows it.
    /// </summary>
    /// <remarks>
    /// Two refusals, and both are deliberate: a runbook the clearance does not name, and a runbook whose every
    /// collection the clearance fails to dominate. The second is a refusal rather than an empty session because a
    /// conversation that can see nothing would answer every question with a confident nothing, and the caller would
    /// have no way to tell that from a corpus that is genuinely empty.
    /// <para>
    /// This does not resolve the runbook: a caller hands in the document, so an unknown or removed reference is refused
    /// before this runs rather than guessed at here.
    /// </para>
    /// </remarks>
    /// <param name="document">The runbook to pin.</param>
    /// <param name="access">The clearance to snapshot.</param>
    /// <param name="uid">The caller the session speaks for.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The opened session, or the refusal.</returns>
    public static SessionOpening Open(
        RunbookDocument document,
        AccessContext access,
        string uid,
        string tenant)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);

        var name = document.Metadata.Name;

        if (!access.PermitsRunbook(name))
        {
            return new SessionRefusal(
                SessionRefusalCodes.RunbookNotPermitted,
                $"the access token does not allow runbook '{name}'");
        }

        var permitted = PermittedCollections(document, access);

        return permitted.Count == 0
            ? new SessionRefusal(
                SessionRefusalCodes.NoVisibleCollections,
                "no collections in this runbook are visible at your access level")
            : new SessionOpened
            {
                Session = new SessionRecord
                {
                    Tenant = tenant,
                    Id = SessionIds.New(),
                    Uid = uid,
                    RunbookRef = Pin(document),
                    Access = access,
                    State = SessionState.Open,
                },
                PermittedCollections = permitted,
            };
    }

    /// <summary>
    /// Pins a runbook as <c>name@version</c>.
    /// </summary>
    /// <remarks>
    /// A name would not be a pin: the next version of the document would silently change what an open conversation
    /// searches, and the earlier turns' answers would have been given under evidence the session no longer reads.
    /// </remarks>
    /// <param name="document">The document.</param>
    /// <returns>The pinned reference.</returns>
    public static string Pin(RunbookDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return $"{document.Metadata.Name}@{document.Metadata.Version}";
    }

    /// <summary>
    /// Answers which of a runbook's collections a clearance permits.
    /// </summary>
    /// <remarks>
    /// The document's own declarations answer this - level and compartments per collection - because that is where a
    /// deployment writes them. Whether a permitted collection can actually be searched is a separate question the
    /// retrieval plane answers, so this list is what a session <em>may</em> read rather than what it will find.
    /// </remarks>
    /// <param name="document">The document.</param>
    /// <param name="access">The clearance.</param>
    /// <returns>The permitted collection names, in the document's order.</returns>
    public static IReadOnlyList<string> PermittedCollections(
        RunbookDocument document,
        AccessContext access)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(access);

        return
        [
            .. document.Spec.EffectiveCollections
                .Where(collection => access.Permits(collection.AccessLevel, collection.Compartments))
                .Select(collection => collection.Name),
        ];
    }
}
