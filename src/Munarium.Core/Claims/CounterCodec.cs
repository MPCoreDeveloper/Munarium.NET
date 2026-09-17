namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// The canonical encoding of a counter, for the ledger payload.
/// </summary>
/// <remarks>
/// A counter is an absolute whole-document total rather than a delta, so recording one is an upsert: the
/// later value wins and the earlier ones are history. That is deliberate - a reader must not have to sum a
/// stream to know how often a pattern was used, because a budget checked against a sum is a budget that
/// changes meaning when an event is lost.
/// </remarks>
public static class CounterCodec
{
    /// <summary>The event type recorded when a counter's total is recorded.</summary>
    public const string RecordedEventType = "counter.recorded";

    /// <summary>
    /// Determines whether an event type carries a counter.
    /// </summary>
    /// <param name="eventType">The event type discriminator.</param>
    /// <returns><see langword="true"/> when the event carries a counter.</returns>
    public static bool IsCounterEvent(string eventType) => eventType is RecordedEventType;

    /// <summary>
    /// Encodes a counter into its canonical payload.
    /// </summary>
    /// <param name="counter">The counter.</param>
    /// <returns>The canonical UTF-8 payload.</returns>
    public static byte[] Encode(CounterTotal counter)
    {
        ArgumentNullException.ThrowIfNull(counter);

        return PayloadJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("counter_key", counter.Key);
            writer.WriteNumber("total", counter.Total);

            if (counter.Budget is { } budget)
            {
                writer.WriteNumber("budget", budget);
                writer.WriteBoolean("budgeted", true);
            }
            else
            {
                writer.WriteBoolean("budgeted", false);
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Decodes a counter.
    /// </summary>
    /// <param name="payload">The canonical payload.</param>
    /// <returns>The counter.</returns>
    /// <exception cref="FormatException">Thrown when the payload is not a canonical counter.</exception>
    public static CounterTotal Decode(ReadOnlySpan<byte> payload)
    {
        using var document = PayloadJson.Read(payload, "counter");
        var root = document.RootElement;

        return new CounterTotal
        {
            Key = PayloadJson.RequiredText(root, "counter_key", "A counter"),
            Total = (ulong)PayloadJson.RequiredNumber(root, "total", "A counter"),
            // An unbudgeted counter is one nobody has set a ceiling for, which is a different statement from
            // a ceiling of zero - so the flag says which of the two it is rather than the absence saying it.
            Budget = PayloadJson.Flag(root, "budgeted")
                ? (ulong)PayloadJson.RequiredNumber(root, "budget", "A budgeted counter")
                : null,
        };
    }
}
