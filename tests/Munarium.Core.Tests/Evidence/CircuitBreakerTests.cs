namespace Munarium.Core.Tests.Evidence;

using Munarium.Evidence;

/// <summary>
/// The breaker that keeps one slow plane from costing a timeout per turn.
/// </summary>
public class CircuitBreakerTests
{
    [Fact]
    public void ItOpensAfterTheThresholdAndClosesOnASuccess()
    {
        var breaker = new CircuitBreaker(3, TimeSpan.FromSeconds(60));

        Assert.False(breaker.IsOpen);

        breaker.RecordFailure();
        breaker.RecordFailure();

        Assert.False(breaker.IsOpen);

        breaker.RecordFailure();

        Assert.True(breaker.IsOpen);

        // A success closes it immediately rather than waiting for the failures to age out: the plane answered, so
        // there is nothing left to protect it from.
        breaker.RecordSuccess();

        Assert.False(breaker.IsOpen);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public void ItReopensTheGateAfterTheCoolOff()
    {
        // A zero cool-off means the deadline is already past: the breaker tripped, and the very next call is allowed
        // to probe. Something that stayed open would never let the plane prove itself recovered.
        var breaker = new CircuitBreaker(1, TimeSpan.Zero);

        breaker.RecordFailure();

        Assert.False(breaker.IsOpen);
        Assert.Equal(1, breaker.ConsecutiveFailures);
    }

    [Fact]
    public void ARefusalDoesNotTripIt()
    {
        // Only outages are recorded through RecordFailure. This holds the other half of that rule: an ordinary
        // sequence of answers - each of which closed the breaker - never accumulates.
        var breaker = new CircuitBreaker(2, TimeSpan.FromMinutes(1));

        for (var answer = 0; answer < 5; answer++)
        {
            breaker.RecordSuccess();
        }

        Assert.False(breaker.IsOpen);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public void ItUsesTheOriginalsDefaults()
    {
        var breaker = new CircuitBreaker();

        for (var failure = 0; failure < CircuitBreaker.DefaultThreshold - 1; failure++)
        {
            breaker.RecordFailure();
        }

        Assert.False(breaker.IsOpen);

        breaker.RecordFailure();

        Assert.True(breaker.IsOpen);
    }
}
