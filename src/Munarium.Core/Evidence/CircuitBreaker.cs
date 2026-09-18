namespace Munarium.Evidence;

using System.Diagnostics;

/// <summary>
/// A per-instance, per-provider breaker for a plane that is a separate deployment.
/// </summary>
/// <remarks>
/// Matrix can be down or slow, and a plane that is down should cost one timeout rather than one per turn: after a
/// threshold of consecutive failures the breaker refuses immediately for a cool-off, then lets the next call through to
/// probe.
/// <para>
/// It is per <em>instance</em> and never per tenant, and the metrics it feeds carry no tenant label for two reasons.
/// The first is cardinality. The second is the real one: a per-tenant series on a shared breaker would report a fact
/// that does not exist, and would let one tenant's scrape reveal that another tenant's traffic had tripped it.
/// </para>
/// <para>
/// State is two counters and one deadline rather than a lock or a timestamp-of-trip, so a read never blocks a
/// concurrent fetch and a trip never has to take a lock to be seen.
/// </para>
/// </remarks>
public sealed class CircuitBreaker
{
    /// <summary>The default consecutive-failure threshold.</summary>
    public const long DefaultThreshold = 5;

    /// <summary>The default cool-off, once the breaker has tripped.</summary>
    public static readonly TimeSpan DefaultCoolOff = TimeSpan.FromSeconds(30);

    private static readonly long Origin = Stopwatch.GetTimestamp();

    private readonly long _threshold;
    private readonly TimeSpan _coolOff;

    private long _consecutiveFailures;
    private long _openUntilMilliseconds;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="threshold">How many consecutive failures trip it.</param>
    /// <param name="coolOff">How long it stays open before a call is allowed to probe.</param>
    public CircuitBreaker(long threshold = DefaultThreshold, TimeSpan? coolOff = null)
    {
        _threshold = threshold > 0
            ? threshold
            : throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Must be positive.");

        _coolOff = coolOff ?? DefaultCoolOff;
    }

    /// <summary>
    /// Gets a value indicating whether calls must be refused without being attempted.
    /// </summary>
    /// <remarks>
    /// A deadline in the past reads as closed, which is what makes a zero cool-off mean "tripped, and immediately
    /// probeable" rather than "tripped for ever".
    /// </remarks>
    public bool IsOpen => Volatile.Read(ref _openUntilMilliseconds) is var until
        && until != 0
        && NowMilliseconds() < until;

    /// <summary>Gets how many failures happened in a row, for the operator view of the plane.</summary>
    public long ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>Records that a call succeeded, which closes the breaker immediately.</summary>
    public void RecordSuccess()
    {
        Volatile.Write(ref _consecutiveFailures, 0);
        Volatile.Write(ref _openUntilMilliseconds, 0);
    }

    /// <summary>
    /// Records that a call failed.
    /// </summary>
    /// <remarks>
    /// Only an outage counts. A refusal is the plane answering correctly - a governed column, a contract this
    /// deployment does not have - and tripping on one of those would take out every other view because one of them
    /// was governed the way it is supposed to be.
    /// </remarks>
    public void RecordFailure()
    {
        if (Interlocked.Increment(ref _consecutiveFailures) >= _threshold)
        {
            Volatile.Write(ref _openUntilMilliseconds, NowMilliseconds() + (long)_coolOff.TotalMilliseconds);
        }
    }

    private static long NowMilliseconds() => (long)Stopwatch.GetElapsedTime(Origin).TotalMilliseconds;
}
