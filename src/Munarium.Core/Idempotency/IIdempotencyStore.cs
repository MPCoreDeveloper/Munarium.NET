namespace Munarium.Idempotency;

/// <summary>
/// What a command answered, kept under the key the caller sent, so a retry is answered rather than done twice.
/// </summary>
/// <remarks>
/// The payload is opaque to the kernel: it is whatever a surface answered - a recorded outcome, in the contract's own
/// shape - and it is replayed verbatim, because the point of an idempotency key is that the caller is told exactly what
/// it was told the first time rather than something equivalent.
/// <para>
/// A key is scoped: the same key on another operation, or on another version, is another request. Without that, a
/// caller's key for one write would silently swallow a different one.
/// </para>
/// <para>
/// Only outcomes that were <em>recorded</em> are kept. A contended write, a malformed request and a refusal that wrote
/// nothing are not answers to remember: a retry of those has to be able to reach the ledger, or a transient failure
/// would become permanent.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Reads what a command answered the first time.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="scope">The operation and the subject it was asked of.</param>
    /// <param name="key">The caller's key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recorded answer, or <see langword="null"/> when that key was not used.</returns>
    ValueTask<string?> FindAsync(
        string tenant,
        string scope,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what a command answered, under its key.
    /// </summary>
    /// <remarks>
    /// The first answer wins: recording a second time under the same key is ignored rather than overwritten, because two
    /// answers to one request is the situation this exists to prevent.
    /// </remarks>
    /// <param name="tenant">The tenant.</param>
    /// <param name="scope">The operation and the subject it was asked of.</param>
    /// <param name="key">The caller's key.</param>
    /// <param name="payload">What the caller was answered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RecordAsync(
        string tenant,
        string scope,
        string key,
        string payload,
        CancellationToken cancellationToken = default);
}
