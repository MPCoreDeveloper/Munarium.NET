namespace Munarium.Promises;

using System.Text.Json.Nodes;
using Munarium.Claims;
using Munarium.Ledger;

/// <summary>
/// The pure half of the promise registry: which open promises are overdue, and where a promise stood
/// at a pin.
/// </summary>
/// <remarks>
/// Registering and fulfilling promises is storage's job; judging them is not, because the judgement has
/// to be reproducible from a snapshot alone. Two rules do the work: a promise is overdue once the unit
/// it was due in is reached, and every open promise is overdue at the end of the work - a promise that
/// outlives its last chance to be paid is the one a reader is most likely to be misled by.
/// </remarks>
public static class PromiseRegistry
{
    /// <summary>The dotted rule identifier recorded with a finding from this check.</summary>
    public const string RuleId = "gate.promise-unfulfilled";

    /// <summary>
    /// Finds the promises that are overdue for the unit being judged.
    /// </summary>
    /// <param name="promises">The promises, as they stand at the pin.</param>
    /// <param name="currentScope">The scope being judged.</param>
    /// <param name="isFinalUnit">Whether this is the last chance the work has to pay its promises.</param>
    /// <returns>One warning finding per open promise that is due.</returns>
    public static IReadOnlyList<GateFinding> FindOverdue(
        IReadOnlyList<Promise> promises,
        string? currentScope,
        bool isFinalUnit)
    {
        ArgumentNullException.ThrowIfNull(promises);

        var findings = new List<GateFinding>();

        foreach (var promise in promises)
        {
            if (promise.Status is not PromiseStatus.Open)
            {
                continue;
            }

            var dueHere = promise.DueScope is { } due && string.Equals(due, currentScope, StringComparison.Ordinal);
            if (!isFinalUnit && !dueHere)
            {
                continue;
            }

            findings.Add(new GateFinding
            {
                RuleId = RuleId,
                Severity = Severity.Warn,
                Message = isFinalUnit
                    ? $"promise '{promise.Key}' is still open at the final unit: {promise.Description}"
                    : $"promise '{promise.Key}' was due at scope '{promise.DueScope}' and is still open",
                ScopePath = currentScope,
                Detail = new JsonObject
                {
                    ["promise_key"] = promise.Key,
                    ["promise_id"] = promise.Id,
                },
            });
        }

        return findings;
    }

    /// <summary>
    /// Reports where a promise stood at a pin.
    /// </summary>
    /// <remarks>
    /// A promise fulfilled after the pin reads back OPEN. Without this a report over a past date would
    /// quietly use answers from the future, which is the one thing a pin exists to prevent; the
    /// fulfilment position is stored for exactly this reading.
    /// </remarks>
    /// <param name="promise">The promise.</param>
    /// <param name="asOfSequence">The pin, or <see langword="null"/> for the present.</param>
    /// <returns>The status at the pin.</returns>
    public static PromiseStatus StatusAt(Promise promise, SequenceNumber? asOfSequence)
    {
        ArgumentNullException.ThrowIfNull(promise);

        return asOfSequence is { } pin &&
            promise.FulfilledSequence is { } fulfilled &&
            promise.Status is PromiseStatus.Fulfilled &&
            fulfilled > pin
                ? PromiseStatus.Open
                : promise.Status;
    }
}
