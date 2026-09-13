using System.Text;

namespace Munarium.Ledger;

/// <summary>
/// An event to append to a stream: a type discriminator and its serialized body.
/// </summary>
/// <param name="Type">The event type discriminator, for example <c>claim.recorded</c>.</param>
/// <param name="Payload">The UTF-8 encoded event body.</param>
public sealed record LedgerEvent(string Type, ReadOnlyMemory<byte> Payload)
{
    /// <summary>
    /// Creates an event from a UTF-8 text body.
    /// </summary>
    /// <param name="type">The event type discriminator.</param>
    /// <param name="body">The event body text.</param>
    /// <returns>The ledger event.</returns>
    public static LedgerEvent FromText(string type, string body) =>
        new(type, Encoding.UTF8.GetBytes(body));
}
