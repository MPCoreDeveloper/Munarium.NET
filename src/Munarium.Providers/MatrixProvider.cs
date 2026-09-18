namespace Munarium.Providers;

using System.Net.Http.Headers;
using System.Text.Json;
using Munarium.Evidence;

/// <summary>
/// Typed tables and counts from Munarium Matrix, over REST.
/// </summary>
/// <remarks>
/// Ground rule 1 in practice: this speaks HTTP to a contract and links against no crate of the plane's, so a change on
/// the far side is a wire break rather than a compile error - which is why the parser is tested against the examples
/// that ship with the contract rather than against JSON written here.
/// <para>
/// Every fetch answers with a <em>refusal block</em> for anything the answer should be able to talk about: a view this
/// profile does not declare, an open circuit, a timeout, a plane that declined. An exception would collapse "the plane
/// declined to say" into "something broke", and those are different answers to give a user.
/// </para>
/// <para>
/// The breaker is per instance and per provider. Its operator metrics carry no tenant label for two reasons, and the
/// second is the real one: unbounded cardinality, and - because a breaker is shared by every tenant on the instance - a
/// tenant label would report a per-tenant fact that does not exist, and would let one tenant's scrape reveal that
/// another tenant's traffic had tripped it.
/// </para>
/// </remarks>
/// <param name="http">The HTTP client to speak to the plane with, owned by the caller.</param>
/// <param name="baseUrl">The plane's base URL.</param>
/// <param name="authorization">The session's own authorization, sent with every intent.</param>
/// <param name="views">The data views this profile declares, by view name.</param>
/// <param name="breaker">The breaker to share, or <see langword="null"/> for a new one.</param>
/// <param name="token">
/// The bearer token to present, when the deployment authenticates this way.
/// </param>
public sealed class MatrixProvider(
    HttpClient http,
    string baseUrl,
    SessionAuthorization authorization,
    IReadOnlyDictionary<string, BoundDataView> views,
    CircuitBreaker? breaker = null,
    string? token = null) : IEvidenceProvider
{
    /// <summary>The deadline a fetch uses when the layer names none.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly SessionAuthorization _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
    private readonly IReadOnlyDictionary<string, BoundDataView> _views = views ?? throw new ArgumentNullException(nameof(views));
    private readonly CircuitBreaker _breaker = breaker ?? new CircuitBreaker();
    private readonly string _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
    private readonly string? _token = token;

    /// <inheritdoc />
    public string Id => EvidencePlanes.Matrix;

    /// <inheritdoc />
    public bool CanServe(string source) =>
        source.StartsWith(EvidencePlanes.MatrixPrefix, StringComparison.Ordinal)
        && _views.ContainsKey(source[EvidencePlanes.MatrixPrefix.Length..]);

    /// <inheritdoc />
    public async ValueTask<EvidenceBlock> FetchAsync(
        EvidenceLayer layer,
        QueryIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(intent);

        // The layer names the view and the profile is the only place the view-to-contract mapping exists, so a layer
        // naming a view nobody declared is a binding defect - refused, never rounded up to a search.
        var bound = ViewOf(layer);

        return bound is null
            ? EvidenceRefusals.Refuse(
                EvidenceRefusalCodes.SourceNotBound,
                "this layer names no data view this profile declares")
            : await AnswerAsync(bound.Value.Name, bound.Value.View, layer, intent, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Answers a layer whose view this profile declares.</summary>
    /// <param name="viewName">The view's name, as the layer named it.</param>
    /// <param name="view">The view's binding.</param>
    /// <param name="layer">The layer, for its deadline.</param>
    /// <param name="intent">What the turn intends to find out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The block, which is a refusal when the question could not be put.</returns>
    private async ValueTask<EvidenceBlock> AnswerAsync(
        string viewName,
        BoundDataView view,
        EvidenceLayer layer,
        QueryIntent intent,
        CancellationToken cancellationToken)
    {
        SemanticSelection? selection = null;
        EvidenceBlock? refusal = null;

        if (_breaker.IsOpen)
        {
            // Named here, unlike the turn-level refusal: the profile's author pinned this view themselves, so the
            // hidden-source rule - which protects sources a caller cannot see - does not apply to it.
            refusal = EvidenceRefusals.Refuse(
                EvidenceRefusalCodes.SourceCircuitOpen,
                "the structured-evidence plane is unavailable and is not being retried yet",
                viewName);
        }
        else if (view.Kind.IsSemantic() && !intent.Selections.TryGetValue(viewName, out selection))
        {
            // A semantic view is asked with names the intent task chose from the lists the view declares. Nothing
            // resolved means nothing to ask with, and sending an empty selection would be a question nobody asked.
            refusal = EvidenceRefusals.Refuse(
                EvidenceRefusalCodes.IntentUnresolved,
                "no semantic selection was resolved for this view; the profile's intent task chooses its measures and "
                    + "dimensions and none was produced",
                viewName);
        }

        return refusal ?? await SendAsync(
            viewName,
            view,
            selection,
            layer.DeadlineMilliseconds is { } milliseconds && milliseconds > 0
                ? TimeSpan.FromMilliseconds(milliseconds)
                : DefaultDeadline,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one intent and turns the answer into a block.</summary>
    private async ValueTask<EvidenceBlock> SendAsync(
        string viewName,
        BoundDataView view,
        SemanticSelection? selection,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var url = string.Concat(_baseUrl.TrimEnd('/'), "/v1/", view.Kind.Route(), "/", view.Contract, "/execute");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(MatrixIntent.BodyOf(view, selection, _authorization)),
        };

        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
        request.Headers.TryAddWithoutValidation("X-Munarium-Uid", _authorization.Uid);

        if (_token is { Length: > 0 } presented)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", presented);
        }

        // A client disconnect drops this call, which drops the request and cancels the connection. Nothing detached
        // keeps spending the plane's budget for an answer nobody will read.
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(deadline);

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, deadlineSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The deadline and not the caller, which is the difference between "the plane is slow" and "nobody is
            // waiting any more".
            _breaker.RecordFailure();

            return EvidenceRefusals.Refuse(
                EvidenceRefusalCodes.SourceTimeout,
                "the structured-evidence plane did not answer in time",
                viewName);
        }
        catch (HttpRequestException)
        {
            _breaker.RecordFailure();

            return EvidenceRefusals.Refuse(
                EvidenceRefusalCodes.SourceUnavailable,
                "the structured-evidence plane could not be reached",
                viewName);
        }

        using (response)
        {
            using var answered = await ReadAsync(response, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A 4xx is the plane answering CORRECTLY - a refused contract, a denied column, an exhausted budget. It
                // is not a breaker failure: tripping on a policy refusal would take out every other view because one of
                // them is governed the way it is supposed to be.
                if ((int)response.StatusCode >= 500)
                {
                    _breaker.RecordFailure();
                }
                else
                {
                    _breaker.RecordSuccess();
                }

                return MatrixResult.RefusalOf(answered.RootElement, viewName);
            }

            _breaker.RecordSuccess();

            return MatrixResult.Parse(answered.RootElement, viewName);
        }
    }

    /// <summary>Reads the answer's body.</summary>
    /// <remarks>
    /// A body that cannot be read as JSON is treated as an empty one, exactly as the original does: the decision is
    /// made on the status and on whatever typed refusal the body carried, and a body that failed to parse carries none.
    /// </remarks>
    private static async ValueTask<JsonDocument> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is JsonException or IOException or HttpRequestException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    /// <summary>
    /// Reads the view a layer pinned, or <see langword="null"/> when this profile declares none of its sources.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The view's name and binding, or <see langword="null"/>.</returns>
    private (string Name, BoundDataView View)? ViewOf(EvidenceLayer layer)
    {
        var declared = layer.Sources
            .Where(source => source.StartsWith(EvidencePlanes.MatrixPrefix, StringComparison.Ordinal))
            .Select(source => source[EvidencePlanes.MatrixPrefix.Length..])
            .Where(_views.ContainsKey)
            .ToList();

        return declared.Count == 0 ? null : (declared[0], _views[declared[0]]);
    }
}
