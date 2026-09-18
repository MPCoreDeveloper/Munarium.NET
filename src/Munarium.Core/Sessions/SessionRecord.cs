namespace Munarium.Sessions;

using Munarium.Access;

/// <summary>
/// One conversation, as the deployment records it.
/// </summary>
/// <remarks>
/// A session pins the runbook it was opened over and snapshots the clearance it was opened with, so neither a runbook
/// upgrade nor a token change mid conversation alters what an ongoing conversation can see. The pinned reference is
/// <c>name@version</c> rather than a name, which is what makes the pin a pin.
/// <para>
/// The timestamps are stamped by the store adapter rather than the kernel, in the same way a source's ingest time is: a
/// clock read inside the kernel would make anything derived from it unreproducible, and this record is read by an
/// operator asking what happened, not by a verdict.
/// </para>
/// </remarks>
public sealed record SessionRecord
{
    /// <summary>Gets the tenant the session belongs to, which is also half of its key.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the session's identity, as <c>ses-</c> plus a ULID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the caller the session speaks for. Every turn is attributed to this uid.</summary>
    public required string Uid { get; init; }

    /// <summary>Gets the pinned runbook reference, <c>name@version</c>.</summary>
    public required string RunbookRef { get; init; }

    /// <summary>Gets the token identity the session was opened with, when the deployment issues one.</summary>
    public string? TokenJti { get; init; }

    /// <summary>Gets the clearance snapshot taken at creation.</summary>
    public required AccessContext Access { get; init; }

    /// <summary>Gets where the session stands.</summary>
    public SessionState State { get; init; } = SessionState.Open;

    /// <summary>Gets when the session was created, stamped by the store.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>Gets when its last turn was recorded, stamped by the store.</summary>
    public string? LastTurnAt { get; init; }

    /// <summary>Gets the runbook's name, which is the pinned reference without its version.</summary>
    public string RunbookName
    {
        get
        {
            var at = RunbookRef.IndexOf('@', StringComparison.Ordinal);

            return at < 0 ? RunbookRef : RunbookRef[..at];
        }
    }
}
