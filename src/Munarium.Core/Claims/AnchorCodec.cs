namespace Munarium.Claims;

using Munarium.Ledger;

/// <summary>
/// The canonical encoding of an anchor, for the ledger payload.
/// </summary>
/// <remarks>
/// A locked anchor is a decision that a detail does not change, so it is written as a record rather than as
/// a patch: the payload says what is locked, to what, in which version and under which evidence, and a
/// reader that only has the payload knows all of it. A release is the same record with the released status,
/// which is what keeps the pair symmetric instead of making the release a special case with its own shape.
/// <para>
/// The position is deliberately absent from the payload: the store assigns it and the read puts it back
/// (<see cref="Decode"/> takes it), so a payload cannot claim a position it was not given - the same
/// reason a claim's identity is handed out rather than accepted.
/// </para>
/// </remarks>
public static class AnchorCodec
{
    /// <summary>The event type recorded when a detail is locked.</summary>
    public const string LockedEventType = "anchor.locked";

    /// <summary>The event type recorded when a lock is lifted.</summary>
    public const string ReleasedEventType = "anchor.released";

    /// <summary>
    /// Determines whether an event type carries an anchor.
    /// </summary>
    /// <param name="eventType">The event type discriminator.</param>
    /// <returns><see langword="true"/> when the event carries an anchor.</returns>
    public static bool IsAnchorEvent(string eventType) => eventType is LockedEventType or ReleasedEventType;

    /// <summary>
    /// Encodes an anchor into its canonical payload.
    /// </summary>
    /// <param name="anchor">The anchor.</param>
    /// <returns>The canonical UTF-8 payload.</returns>
    public static byte[] Encode(Anchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        return PayloadJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("anchor_id", anchor.Id);
            writer.WriteString("version_id", anchor.VersionId);
            writer.WriteString("detail_key", anchor.DetailKey);
            writer.WriteString("locked_value", anchor.LockedValue);
            PayloadJson.Optional(writer, "locked_at_scope", anchor.LockedAtScope);
            writer.WriteNumber("status", (int)anchor.Status);
            PayloadJson.Optional(writer, "evidence", anchor.EvidenceJson);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Decodes an anchor, stamping it with the position its event landed at.
    /// </summary>
    /// <param name="payload">The canonical payload.</param>
    /// <param name="position">The position the event landed at.</param>
    /// <returns>The anchor.</returns>
    /// <exception cref="FormatException">Thrown when the payload is not a canonical anchor.</exception>
    public static Anchor Decode(ReadOnlySpan<byte> payload, SequenceNumber position)
    {
        using var document = PayloadJson.Read(payload, "anchor");
        var root = document.RootElement;

        return new Anchor
        {
            Id = PayloadJson.RequiredText(root, "anchor_id", "An anchor"),
            VersionId = PayloadJson.RequiredText(root, "version_id", "An anchor"),
            DetailKey = PayloadJson.RequiredText(root, "detail_key", "An anchor"),
            LockedValue = PayloadJson.RequiredText(root, "locked_value", "An anchor"),
            LockedAtScope = PayloadJson.Text(root, "locked_at_scope"),
            Status = StatusOf(PayloadJson.RequiredNumber(root, "status", "An anchor")),
            Sequence = position,
            EvidenceJson = PayloadJson.Text(root, "evidence"),
        };
    }

    // Bounds-checked rather than cast: an unknown status is refused instead of becoming a plausible one.
    private static AnchorStatus StatusOf(long value) => value is >= (int)AnchorStatus.Locked and <= (int)AnchorStatus.Released
        ? (AnchorStatus)value
        : throw new FormatException($"Unknown anchor status '{value}'.");
}
