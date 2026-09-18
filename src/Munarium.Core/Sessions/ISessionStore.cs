namespace Munarium.Sessions;

/// <summary>
/// Persistence for the session plane.
/// </summary>
/// <remarks>
/// A seam of its own rather than a corner of the ledger's: a conversation is not a claim about the world, and a turn is
/// not a verdict - putting them in the ledger would make asking a question part of the record governance reasons over.
/// It is also not the evidence plane's store: an artifact is a promise about bytes, a turn is a record of a
/// conversation, and the two have different retention stories.
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// Records a new session.
    /// </summary>
    /// <remarks>
    /// Idempotent by identity: storing the same session id twice leaves the first row, because a session's clearance is
    /// the one taken at creation and re-storing it would let a later call change what an ongoing conversation sees.
    /// </remarks>
    /// <param name="session">The session to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session as stored, with its timestamps filled in.</returns>
    ValueTask<SessionRecord> CreateAsync(
        SessionRecord session,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a session.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="sessionId">The session's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session, or <see langword="null"/> when there is none.</returns>
    ValueTask<SessionRecord?> GetAsync(
        string tenant,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a turn and answers the ordinal it was given.
    /// </summary>
    /// <remarks>
    /// The ordinal is allocated <em>inside</em> the append rather than computed by the caller, because two concurrent
    /// turns computing the same next ordinal is precisely the situation a caller cannot see. Whatever the store has to
    /// do to make that atomic - a computed insert, a lock, a retry - stays behind this seam.
    /// </remarks>
    /// <param name="turn">The turn to record; its <see cref="TurnRecord.Ordinal"/> is ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ordinal the turn settled at, one-based.</returns>
    ValueTask<int> AppendTurnAsync(TurnRecord turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a session.
    /// </summary>
    /// <remarks>
    /// Closing is one-way, so this reports whether it happened rather than performing it silently twice: a replayed
    /// close is visible instead of being indistinguishable from a first one.
    /// <para>
    /// No timestamp is taken: the original's close does not record one either, and a kernel that asked for a clock it
    /// did not store would be reading one for nothing.
    /// </para>
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="sessionId">The session's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when the session is unknown or was already closed.</returns>
    ValueTask<bool> CloseAsync(
        string tenant,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a session's turns, oldest first.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="sessionId">The session's identity.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The turns, in the order they were asked.</returns>
    ValueTask<IReadOnlyList<TurnRecord>> TurnsAsync(
        string tenant,
        string sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a caller's sessions, newest first.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="uid">The caller.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sessions, newest first.</returns>
    ValueTask<IReadOnlyList<SessionRecord>> RecentAsync(
        string tenant,
        string uid,
        int limit,
        CancellationToken cancellationToken = default);
}
