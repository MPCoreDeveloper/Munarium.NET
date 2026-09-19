namespace Munarium.Server.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Munarium.Wire;

/// <summary>
/// The session and turn operations over the JSON surface: opening a conversation over an applied runbook, asking it
/// something, reading the transcript back, and closing it.
/// </summary>
public class SessionApiTests : IClassFixture<MunariumApiFactory>
{
    private readonly HttpClient _client;

    /// <summary>Creates the test over the real application.</summary>
    /// <param name="factory">The application under test.</param>
    public SessionApiTests(MunariumApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _client = factory.CreateClient();
    }

    /// <summary>
    /// The whole conversation, in the order a client drives it: apply, open, ask, read, close. The turn asks for no
    /// completion, which is the deployment's only option today - it has no model configured - and the transcript still
    /// records what it searched and what it found.
    /// </summary>
    [Fact]
    public async Task AConversationRunsFromApplyToClose()
    {
        var runbookRef = await ApplyWorkedExample();

        var opened = await _client.PostAsync(
            new Uri($"/v1/runbooks/{Name(runbookRef)}/sessions", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        var session = await opened.Content.ReadFromJsonAsync(WireJson.Default.WireSessionCreated);

        Assert.NotNull(session);
        Assert.StartsWith("ses-", session.SessionId, StringComparison.Ordinal);
        Assert.Equal(runbookRef, session.RunbookRef);
        Assert.NotEmpty(session.PermittedCollections);

        var turn = await AskAsync(session.SessionId, "what does the policy say?");

        Assert.Equal(1, turn.Ordinal);
        Assert.Equal("what does the policy say?", turn.Query);
        Assert.NotEmpty(turn.CollectionsSearched);
        Assert.Null(turn.Hierarchy);

        // The provenance travels even when the corpus answered with nothing: it describes the search that found nothing,
        // which is what tells a reader that the corpus is empty rather than that the question was ignored.
        Assert.Single(turn.Envelopes);

        var transcript = await _client.GetFromJsonAsync(
            new Uri($"/v1/sessions/{session.SessionId}", UriKind.Relative),
            WireJson.Default.WireSession);

        Assert.NotNull(transcript);
        Assert.Equal("open", transcript.State);
        Assert.Equal(session.RunbookRef, transcript.RunbookRef);

        var recorded = Assert.Single(transcript.Turns);

        Assert.Equal(1, recorded.Ordinal);
        Assert.Equal("what does the policy say?", recorded.Query);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Hits));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Envelope));

        var closed = await _client.PostAsync(
            new Uri($"/v1/sessions/{session.SessionId}/close", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var state = await closed.Content.ReadFromJsonAsync(WireJson.Default.WireSessionClosed);

        Assert.Equal("closed", state?.State);

        // A closed conversation takes no further turns, and says so rather than answering.
        var refused = await _client.PostAsJsonAsync(
            new Uri($"/v1/sessions/{session.SessionId}/turns", UriKind.Relative),
            new WireTurnRequest("anything else", Complete: false),
            WireJson.Default.WireTurnRequest);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// A turn that asks for an answer the deployment cannot produce says so: the request is well formed, the deployment
    /// is not equipped, and 503 is what that is.
    /// </summary>
    [Fact]
    public async Task ATurnThatNeedsAModelSaysTheDeploymentHasNone()
    {
        var runbookRef = await ApplyWorkedExample();

        var opened = await _client.PostAsync(
            new Uri($"/v1/runbooks/{Name(runbookRef)}/sessions", UriKind.Relative),
            content: null);

        var session = await opened.Content.ReadFromJsonAsync(WireJson.Default.WireSessionCreated);
        Assert.NotNull(session);

        var response = await _client.PostAsJsonAsync(
            new Uri($"/v1/sessions/{session.SessionId}/turns", UriKind.Relative),
            new WireTurnRequest("how many contracts lapse?", Complete: true),
            WireJson.Default.WireTurnRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync(WireJson.Default.WireProblem);

        Assert.Equal(MunariumOperations.NoCompletionModelProblem, problem?.Type);
    }

    /// <summary>
    /// The streamed turn is the same turn, reported while it runs: a frame per stage, then exactly one that says how it
    /// ended.
    /// </summary>
    /// <remarks>
    /// The stream is read to completion rather than consumed frame by frame, which still proves the part a client
    /// depends on: the frames are the right ones, in the order they were written, with the terminal frame carrying what
    /// the unary route would have answered.
    /// </remarks>
    [Fact]
    public async Task AStreamedTurnReportsProgressThenItsResult()
    {
        var runbookRef = await ApplyWorkedExample();

        var opened = await _client.PostAsync(
            new Uri($"/v1/runbooks/{Name(runbookRef)}/sessions", UriKind.Relative),
            content: null);

        var session = await opened.Content.ReadFromJsonAsync(WireJson.Default.WireSessionCreated);
        Assert.NotNull(session);

        var response = await _client.PostAsJsonAsync(
            new Uri($"/v1/sessions/{session.SessionId}/turns/stream", UriKind.Relative),
            new WireTurnRequest("what does the policy say?", Complete: false),
            WireJson.Default.WireTurnRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        // The frames are the original's: progress, then exactly one of done or error.
        Assert.Contains("event: progress", body, StringComparison.Ordinal);
        Assert.Contains("\"stage\":\"merge\"", body, StringComparison.Ordinal);
        Assert.Contains("event: done", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event: error", body, StringComparison.Ordinal);

        // A stream that reported a stage after its result would be a stream whose frames are in the wrong order, and the
        // order is the one thing a reader of a live stream has to be able to trust.
        Assert.True(
            body.IndexOf("event: progress", StringComparison.Ordinal)
                < body.IndexOf("event: done", StringComparison.Ordinal),
            "progress must be reported before the turn's result");

        var turn = JsonSerializer.Deserialize(DataLines(body).Last(), WireJson.Default.WireTurnResponse);

        Assert.NotNull(turn);
        Assert.Equal(1, turn.Ordinal);
        Assert.Equal("what does the policy say?", turn.Query);
        Assert.Null(turn.Hierarchy);
    }

    /// <summary>
    /// A streamed turn that cannot run still ends by saying so, because its status line is already sent.
    /// </summary>
    /// <remarks>
    /// This is the difference between the two routes and the reason both exist: the unary route answers a closed
    /// conversation with 409, and the streamed one opens the stream and then ends it with the same problem, because a
    /// client that has already received a 200 cannot be told anything else.
    /// </remarks>
    [Fact]
    public async Task AStreamedTurnThatCannotRunEndsWithAnErrorFrame()
    {
        var runbookRef = await ApplyWorkedExample();

        var opened = await _client.PostAsync(
            new Uri($"/v1/runbooks/{Name(runbookRef)}/sessions", UriKind.Relative),
            content: null);

        var session = await opened.Content.ReadFromJsonAsync(WireJson.Default.WireSessionCreated);
        Assert.NotNull(session);

        var closed = await _client.PostAsync(
            new Uri($"/v1/sessions/{session.SessionId}/close", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var response = await _client.PostAsJsonAsync(
            new Uri($"/v1/sessions/{session.SessionId}/turns/stream", UriKind.Relative),
            new WireTurnRequest("anything else", Complete: false),
            WireJson.Default.WireTurnRequest);

        // The stream opened before the turn was attempted, so the failure cannot be a status: it is the last frame.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: error", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event: done", body, StringComparison.Ordinal);

        var problem = JsonSerializer.Deserialize(DataLines(body).Last(), WireJson.Default.WireProblem);

        Assert.NotNull(problem);
        Assert.Equal(409, problem.Status);
    }

    /// <summary>The data lines of a streamed body, in the order its frames carry them.</summary>
    /// <param name="body">The body.</param>
    /// <returns>One entry per frame, without its prefix.</returns>
    private static List<string> DataLines(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return
        [
            .. body
                .Split('\n')
                .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                .Select(line => line["data: ".Length..]),
        ];
    }

    private async Task<WireTurnResponse> AskAsync(string sessionId, string query)
    {
        var response = await _client.PostAsJsonAsync(
            new Uri($"/v1/sessions/{sessionId}/turns", UriKind.Relative),
            new WireTurnRequest(query, Complete: false),
            WireJson.Default.WireTurnRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var turn = await response.Content.ReadFromJsonAsync(WireJson.Default.WireTurnResponse);

        return Assert.IsType<WireTurnResponse>(turn);
    }

    /// <summary>Applies the worked example and answers the reference it was pinned to.</summary>
    /// <returns>The reference.</returns>
    private async Task<string> ApplyWorkedExample()
    {
        var response = await _client.PostAsJsonAsync(
            new Uri("/v1/runbooks", UriKind.Relative),
            new WireRunbookApply(Example()),
            WireJson.Default.WireRunbookApply);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var applied = await response.Content.ReadFromJsonAsync(WireJson.Default.WireAppliedRunbook);

        return Assert.IsType<WireAppliedRunbook>(applied).RunbookRef;
    }

    /// <summary>A reference without its version, which is what a session is opened by name with.</summary>
    /// <param name="runbookRef">The reference.</param>
    /// <returns>The name.</returns>
    private static string Name(string runbookRef) => runbookRef[..runbookRef.IndexOf('@', StringComparison.Ordinal)];

    /// <summary>Reads the worked example the original ships, which is the conformance fixture.</summary>
    /// <returns>The YAML, as written.</returns>
    private static string Example()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "contract", "lab", "example", "runbook.yaml");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("contract/lab/example/runbook.yaml was not found above the test binary.");
    }
}
