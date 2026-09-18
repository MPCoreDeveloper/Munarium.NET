namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// An authoring write lost every race for the head.
/// </summary>
/// <remarks>
/// Modelled rather than thrown, for the same reason a claim write is: a lock or a promise that lost to a
/// concurrent writer is not data loss, it is a retryable answer that both transports can carry.
/// </remarks>
/// <param name="Expected">The head the writer last observed.</param>
/// <param name="Actual">The head the stream actually held.</param>
public sealed record WriteContended(SequenceNumber Expected, SequenceNumber Actual);

/// <summary>
/// The outcome of locking a detail: the lock as recorded, or the race that stopped it.
/// </summary>
public readonly union AnchorOutcome(Anchor, WriteContended);

/// <summary>
/// The outcome of releasing a lock: the release as recorded, the fact that nothing was locked, or a race.
/// </summary>
/// <remarks>
/// "Nothing was locked" is its own case rather than a recorded release, because recording a release for a detail
/// that was never locked would put a successful-looking write in the ledger for an operation that changed
/// nothing - and an auditor reading the anchors plane would never see it.
/// </remarks>
public readonly union AnchorReleaseOutcome(Anchor, AnchorNotLocked, WriteContended);

/// <summary>A release that had nothing to release.</summary>
/// <param name="DetailKey">The detail that was not locked.</param>
public sealed record AnchorNotLocked(string DetailKey);

/// <summary>
/// The outcome of registering a promise: the promise as recorded, or the race that stopped it.
/// </summary>
public readonly union PromiseOutcome(Promise, WriteContended);

/// <summary>
/// The outcome of fulfilling a promise: the promise as fulfilled, the fact that none was open, or a race.
/// </summary>
public readonly union FulfilOutcome(Promise, PromiseNotOpen, WriteContended);

/// <summary>
/// The outcome of recording a counter: the total as recorded, or the race that stopped it.
/// </summary>
/// <remarks>
/// There is no "nothing to record" case: a counter is a number about the text, and zero is a number. A pattern used
/// no times is a fact worth keeping, which is why the wire counts in int64 and the plane has no absent state.
/// </remarks>
public readonly union CounterOutcome(CounterTotal, WriteContended);

/// <summary>A fulfilment that found nothing open.</summary>
/// <remarks>
/// Upstream answers this as <c>fulfilled: false</c>, which is the same statement in a smaller vocabulary: the
/// caller is told what happened rather than given an error for asking.
/// </remarks>
/// <param name="Key">The promise key that was not open.</param>
public sealed record PromiseNotOpen(string Key);
