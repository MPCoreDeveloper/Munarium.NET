namespace Munarium.Evidence;

/// <summary>
/// The identity the server assigns an artifact at seal.
/// </summary>
/// <remarks>
/// The original mints <c>ev-</c> plus a random 128-bit value in hex, and this mints the same shape for the reason it
/// implies: a citation carries nothing but the id, so an id that could be guessed would hand out the existence of
/// artifacts nobody was told about - and a counter would hand them out in order.
/// <para>
/// It is a property of the deployment rather than a claim by the caller. A manifest arrives without an id and every read
/// has one, which is what makes the id something a reader can trust rather than something a producer asserted.
/// </para>
/// </remarks>
public static class EvidenceIds
{
    /// <summary>The prefix every evidence identity carries.</summary>
    public const string Prefix = "ev-";

    /// <summary>Mints an identity.</summary>
    /// <returns>The identity, as <c>ev-</c> plus thirty-two lowercase hex digits.</returns>
    public static string New() => string.Concat(Prefix, Guid.NewGuid().ToString("N"));
}
