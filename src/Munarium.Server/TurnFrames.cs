namespace Munarium.Server;

using System.Text.Json;
using Munarium.Wire;

/// <summary>
/// Writes the streamed turn's frames.
/// </summary>
/// <remarks>
/// One frame is the event's name, its data, and the blank line that ends it - which is the whole of the format, and the
/// reason this is a few lines of code rather than a library: the names and the payloads come from the contract, and
/// nothing here decides what an event is.
/// </remarks>
public static class TurnFrames
{
    /// <summary>
    /// Writes one frame, and says whether the client was still there to take it.
    /// </summary>
    /// <remarks>
    /// Writes are deliberately not cancelled by the request's own token: a client that hung up has to be
    /// <em>detected</em> rather than obeyed, because the turn it stopped listening to has already been paid for and still
    /// has to be recorded. The two failures that mean "nobody is reading" are a broken pipe and a disposed response, and
    /// both are answered with a false rather than an exception.
    /// </remarks>
    /// <param name="response">The response to write to.</param>
    /// <param name="name">The event's name.</param>
    /// <param name="data">The event's data, already encoded.</param>
    /// <returns><see langword="true"/> when the frame went out; <see langword="false"/> when the client is gone.</returns>
    public static async ValueTask<bool> WriteFrameAsync(HttpResponse response, string name, string data)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            await response.WriteAsync($"event: {name}\ndata: {data}\n\n", CancellationToken.None).ConfigureAwait(false);
            await response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Encodes one progress event with its own stage's shape.
    /// </summary>
    /// <remarks>
    /// The event's type is its stage, so the payload is written by the type's own serializer rather than by a generic one
    /// that would have to decide the shape at run time - and a stage that gains a field cannot lose it here.
    /// </remarks>
    /// <param name="progress">The event to encode.</param>
    /// <returns>The JSON the frame carries.</returns>
    public static string Json(WireTurnEvent progress) => progress switch
    {
        WireTurnProfileEvent profile => JsonSerializer.Serialize(profile, WireJson.Default.WireTurnProfileEvent),
        WireTurnLayerStartEvent started =>
            JsonSerializer.Serialize(started, WireJson.Default.WireTurnLayerStartEvent),
        WireTurnLayerSourceEvent source =>
            JsonSerializer.Serialize(source, WireJson.Default.WireTurnLayerSourceEvent),
        WireTurnLayerCompleteEvent completed =>
            JsonSerializer.Serialize(completed, WireJson.Default.WireTurnLayerCompleteEvent),
        WireTurnCoverageEvent coverage =>
            JsonSerializer.Serialize(coverage, WireJson.Default.WireTurnCoverageEvent),
        WireTurnComposeEvent composed => JsonSerializer.Serialize(composed, WireJson.Default.WireTurnComposeEvent),
        WireTurnModelEvent model => JsonSerializer.Serialize(model, WireJson.Default.WireTurnModelEvent),
        WireTurnMergeEvent merged => JsonSerializer.Serialize(merged, WireJson.Default.WireTurnMergeEvent),
        WireTurnCompletionEvent completion =>
            JsonSerializer.Serialize(completion, WireJson.Default.WireTurnCompletionEvent),
        WireTurnVerifyEvent verified => JsonSerializer.Serialize(verified, WireJson.Default.WireTurnVerifyEvent),
    };
}
