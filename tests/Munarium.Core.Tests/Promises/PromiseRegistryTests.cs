namespace Munarium.Core.Tests.Promises;

using Munarium.Claims;
using Munarium.Core.Tests.Support;
using Munarium.Ledger;
using Munarium.Promises;

/// <summary>
/// Tests for the promise registry's judgement: what is overdue, and where a promise stood at a pin.
/// </summary>
public class PromiseRegistryTests
{
    [Fact]
    public void APromiseDueInThisScopeIsOverdue()
    {
        var promises = new[] { Promise("a", due: "ch3") };

        Assert.Single(PromiseRegistry.FindOverdue(promises, "ch3", isFinalUnit: false));
        Assert.Empty(PromiseRegistry.FindOverdue(promises, "ch2", isFinalUnit: false));
    }

    [Fact]
    public void TheFinalUnitFlagsEveryOpenPromise()
    {
        var promises = new[]
        {
            Promise("a", due: null),
            Promise("b", due: "ch9"),
            Promise("c", due: null, status: PromiseStatus.Fulfilled, fulfilledSequence: 4),
        };

        Assert.Equal(2, PromiseRegistry.FindOverdue(promises, "ch5", isFinalUnit: true).Count);
    }

    [Fact]
    public void AnOverdueFindingNamesThePromiseAndItsScope()
    {
        var finding = Assert.Single(PromiseRegistry.FindOverdue([Promise("a", due: "ch3")], "ch3", isFinalUnit: false));

        Assert.Equal(PromiseRegistry.RuleId, finding.RuleId);
        Assert.Equal(Severity.Warn, finding.Severity);
        Assert.Equal("ch3", finding.ScopePath);
        Assert.Equal("a", finding.Detail!["promise_key"]!.GetValue<string>());
    }

    /// <summary>
    /// A promise fulfilled after the pin reads back open - the pin semantic, applied to promises.
    /// </summary>
    [Fact]
    public void AFulfilmentAfterThePinReadsBackOpen()
    {
        var promise = Promise("a", due: null, status: PromiseStatus.Fulfilled, fulfilledSequence: 8);

        Assert.Equal(PromiseStatus.Open, PromiseRegistry.StatusAt(promise, new SequenceNumber(5)));
        Assert.Equal(PromiseStatus.Fulfilled, PromiseRegistry.StatusAt(promise, new SequenceNumber(9)));
        Assert.Equal(PromiseStatus.Fulfilled, PromiseRegistry.StatusAt(promise, null));
    }

    private static Promise Promise(
        string key,
        string? due,
        PromiseStatus status = PromiseStatus.Open,
        long? fulfilledSequence = null) => new()
        {
            Id = $"p-{key}",
            VersionId = ClaimFixture.VersionId,
            Key = key,
            Kind = "setup",
            Description = $"payoff for {key}",
            OriginScope = "ch1",
            DueScope = due,
            Status = status,
            Sequence = new SequenceNumber(1),
            FulfilledSequence = fulfilledSequence is { } position ? new SequenceNumber(position) : null,
        };
}
