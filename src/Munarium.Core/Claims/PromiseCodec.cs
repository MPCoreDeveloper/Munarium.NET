namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// The canonical encoding of a promise, for the ledger payload.
/// </summary>
/// <remarks>
/// Both events carry the whole record, for the reason an anchor's pair does: a payload that a reader cannot
/// understand on its own is a payload that needs another read to interpret, and the fulfilment's position is
/// again the store's to assign rather than the payload's to claim.
/// <para>
/// A fulfilment without a registration is not an error at this layer - the fold simply has nothing to apply
/// it to, exactly as a correction of something never established is surfaced rather than refused. The gate
/// that judges promises reads the folded state, and a state nobody set up cannot be judged.
/// </para>
/// </remarks>
public static class PromiseCodec
{
    /// <summary>The event type recorded when a promise is registered.</summary>
    public const string RegisteredEventType = "promise.registered";

    /// <summary>The event type recorded when a promise is fulfilled, expired or violated.</summary>
    public const string FulfilledEventType = "promise.fulfilled";

    /// <summary>
    /// Determines whether an event type carries a promise.
    /// </summary>
    /// <param name="eventType">The event type discriminator.</param>
    /// <returns><see langword="true"/> when the event carries a promise.</returns>
    public static bool IsPromiseEvent(string eventType) => eventType is RegisteredEventType or FulfilledEventType;

    /// <summary>
    /// Encodes a promise into its canonical payload.
    /// </summary>
    /// <param name="promise">The promise.</param>
    /// <returns>The canonical UTF-8 payload.</returns>
    public static byte[] Encode(Promise promise)
    {
        ArgumentNullException.ThrowIfNull(promise);

        return PayloadJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("promise_id", promise.Id);
            writer.WriteString("version_id", promise.VersionId);
            writer.WriteString("key", promise.Key);
            writer.WriteString("kind", promise.Kind);
            writer.WriteString("description", promise.Description);
            PayloadJson.Optional(writer, "origin_scope", promise.OriginScope);
            PayloadJson.Optional(writer, "due_scope", promise.DueScope);
            writer.WriteNumber("status", (int)promise.Status);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Decodes a registration, which is open by definition until something fulfils it.
    /// </summary>
    /// <param name="payload">The canonical payload.</param>
    /// <param name="position">The position the registration landed at.</param>
    /// <returns>The promise.</returns>
    /// <exception cref="FormatException">Thrown when the payload is not a canonical promise.</exception>
    public static Promise DecodeRegistered(ReadOnlySpan<byte> payload, SequenceNumber position) =>
        Decode(payload, position, fulfilledAt: null);

    /// <summary>
    /// Decodes a fulfilment, stamping it with the position it landed at.
    /// </summary>
    /// <param name="payload">The canonical payload.</param>
    /// <param name="position">The position the fulfilment landed at.</param>
    /// <returns>The promise, with the position it was fulfilled at.</returns>
    /// <exception cref="FormatException">Thrown when the payload is not a canonical promise.</exception>
    public static Promise DecodeFulfilled(ReadOnlySpan<byte> payload, SequenceNumber position) =>
        Decode(payload, position, fulfilledAt: position);

    private static Promise Decode(ReadOnlySpan<byte> payload, SequenceNumber position, SequenceNumber? fulfilledAt)
    {
        using var document = PayloadJson.Read(payload, "promise");
        var root = document.RootElement;

        return new Promise
        {
            Id = PayloadJson.RequiredText(root, "promise_id", "A promise"),
            VersionId = PayloadJson.RequiredText(root, "version_id", "A promise"),
            Key = PayloadJson.RequiredText(root, "key", "A promise"),
            Kind = PayloadJson.RequiredText(root, "kind", "A promise"),
            Description = PayloadJson.RequiredText(root, "description", "A promise"),
            OriginScope = PayloadJson.Text(root, "origin_scope"),
            DueScope = PayloadJson.Text(root, "due_scope"),
            Status = StatusOf(PayloadJson.RequiredNumber(root, "status", "A promise"), fulfilledAt),
            Sequence = position,
            FulfilledSequence = fulfilledAt,
        };
    }

    // A registration is open whatever the payload says: the status of a promise nobody has fulfilled yet has
    // one honest value, and accepting another one would let a writer claim a fulfilment it did not record.
    private static PromiseStatus StatusOf(long value, SequenceNumber? fulfilledAt) =>
        fulfilledAt is null ? PromiseStatus.Open : DeclaredStatus(value);

    private static PromiseStatus DeclaredStatus(long value) =>
        value is >= (int)PromiseStatus.Open and <= (int)PromiseStatus.Violated
            ? (PromiseStatus)value
            : throw new FormatException($"Unknown promise status '{value}'.");
}
