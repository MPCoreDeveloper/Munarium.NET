namespace Munarium.Server;

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Munarium.Access;
using Munarium.Evidence;
using Munarium.Wire;

/// <summary>
/// The JSON/HTTP surface of the wire contract.
/// </summary>
/// <remarks>
/// Every endpoint here is an adapter and nothing more: it binds the contract's shape, calls the one
/// operation surface, and encodes the answer. No endpoint decides anything about the ledger, which
/// is why this surface and the gRPC one generated from the same specification cannot disagree.
/// </remarks>
public static class MunariumEndpoints
{
    /// <summary>Maps the contract's operations.</summary>
    /// <param name="app">The application to map onto.</param>
    /// <returns>The endpoint route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapMunarium(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/healthz", () => TypedResults.Ok(MunariumOperations.Health()));

        // The capability plane: the identity provider in front authenticates people, and this is where the authority it
        // asserts is exchanged for a short-lived credential. Nothing here decides anything about the ledger; it mints what
        // the contract asks for, or refuses by naming the rule that refused.
        app.MapPost(
            "/v1/access-tokens",
            (
                WireAccessTokenRequest request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = operations.IssueAccessToken(
                    request,
                    MunariumKernel.AccessSecret,
                    DateTimeOffset.UtcNow);

                IResult answer = result switch
                {
                    WireAccessToken issued => TypedResults.Json(
                        issued, WireJson.Default.WireAccessToken),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });


        app.MapPost(
            "/v1/versions",
            async (
                WireVersionRequest request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.CreateVersionAsync(request, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireVersion version => TypedResults.Json(version, WireJson.Default.WireVersion),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/versions/{version_id}/head",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations.GetHeadAsync(versionId, cancellationToken).ConfigureAwait(false));

        app.MapGet(
            "/v1/versions/{version_id}/lineage",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var lineage = await operations.GetLineageAsync(versionId, cancellationToken).ConfigureAwait(false);

                // An empty lineage means no such version, because a version that exists always has itself
                // in its own lineage. Answering 404 keeps "unknown" out of the data it would contaminate.
                IResult answer = lineage.Versions.Count == 0
                    ? TypedResults.Json(
                        new WireProblem(
                            "https://munarium.dev/problems/unknown-version",
                            $"no version '{versionId}' exists.",
                            Status: 404,
                            ExpectedHead: 0,
                            ActualHead: 0),
                        WireJson.Default.WireProblem,
                        statusCode: 404)
                    : TypedResults.Json(lineage, WireJson.Default.WireVersionLineage);

                return answer;
            });

        app.MapPost(
            "/v1/versions/{version_id}/claims",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                WireClaimProposal proposal,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .ProposeClaimAsync(versionId, proposal, cancellationToken)
                    .ConfigureAwait(false);

                // Contention is a transport-level answer rather than a recorded outcome: 409 with the
                // problem, which is the JSON twin of the ABORTED a gRPC client would get.
                IResult answer = result switch
                {
                    WireClaimOutcome outcome => TypedResults.Json(outcome, WireJson.Default.WireClaimOutcome),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/versions/{version_id}/claim-batches",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                WireClaimBatchRequest request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .ProposeClaimBatchAsync(versionId, request, cancellationToken)
                    .ConfigureAwait(false);

                // A batch that was judged and recorded is a 200 even when it carries a disputed claim: the
                // ledger recorded the refusal rather than losing the write. Only a batch that was not
                // recorded - a request that was not understood, or a pin that lost - is a problem.
                IResult answer = result switch
                {
                    WireClaimBatchOutcome outcome => TypedResults.Json(
                        outcome, WireJson.Default.WireClaimBatchOutcome),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/versions/{version_id}/findings",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                [FromQuery(Name = "as_of")] long? asOf,
                [FromQuery(Name = "severity")] string? severity,
                [FromQuery(Name = "rule_id")] string? ruleId,
                [FromQuery(Name = "rule_prefix")] string? rulePrefix,
                [FromQuery(Name = "limit")] int? limit,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations
                    .ListFindingsAsync(
                        versionId,
                        asOf ?? 0,
                        severity,
                        ruleId,
                        rulePrefix,
                        limit ?? 0,
                        cancellationToken)
                    .ConfigureAwait(false));

        app.MapPost(
            "/v1/versions/{version_id}/anchors",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                WireAnchorLock request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.LockAnchorAsync(versionId, request, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireAnchor anchor => TypedResults.Json(anchor, WireJson.Default.WireAnchor),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/versions/{version_id}/anchors",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                [FromQuery(Name = "as_of")] long? asOf,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations
                    .ListAnchorsAsync(versionId, asOf ?? 0, cancellationToken)
                    .ConfigureAwait(false));

        app.MapPost(
            "/v1/versions/{version_id}/anchors/{detail_key}/release",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                [FromRoute(Name = "detail_key")] string detailKey,
                [FromQuery(Name = "idempotency_key")] string? idempotencyKey,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .ReleaseAnchorAsync(versionId, detailKey, idempotencyKey, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireAnchorRelease released => TypedResults.Json(released, WireJson.Default.WireAnchorRelease),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/versions/{version_id}/promises",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                WirePromiseRegistration request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.OpenPromiseAsync(versionId, request, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WirePromise promise => TypedResults.Json(promise, WireJson.Default.WirePromise),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/versions/{version_id}/promises",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                [FromQuery(Name = "as_of")] long? asOf,
                [FromQuery(Name = "status")] string? status,
                [FromQuery(Name = "overdue_scope")] string? overdueScope,
                [FromQuery(Name = "final")] bool? isFinalUnit,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations
                    .ListPromisesAsync(versionId, asOf ?? 0, status, overdueScope, isFinalUnit ?? false, cancellationToken)
                    .ConfigureAwait(false));

        app.MapPost(
            "/v1/versions/{version_id}/promises/{key}/fulfill",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                [FromRoute(Name = "key")] string key,
                [FromQuery(Name = "idempotency_key")] string? idempotencyKey,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .FulfilPromiseAsync(versionId, key, idempotencyKey, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WirePromiseFulfilment fulfilled => TypedResults.Json(
                        fulfilled, WireJson.Default.WirePromiseFulfilment),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/versions/{version_id}/counters",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                WireCounterRecording request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.RecordCounterAsync(versionId, request, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireCounter counter => TypedResults.Json(counter, WireJson.Default.WireCounter),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/versions/{version_id}/counters",
            async (
                [FromRoute(Name = "version_id")] string versionId,
                [FromQuery(Name = "as_of")] long? asOf,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations
                    .ListCountersAsync(versionId, asOf ?? 0, cancellationToken)
                    .ConfigureAwait(false));

        app.MapGet(
            "/v1/snapshots",
            async (
                [FromQuery(Name = "version_id")] string? versionId,
                [FromQuery(Name = "as_of")] long? asOf,
                [FromQuery(Name = "scope")] string? scope,
                [FromQuery(Name = "fact_limit")] int? factLimit,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations
                    .LoadSnapshotAsync(versionId ?? string.Empty, asOf ?? 0, scope, factLimit ?? 0, cancellationToken)
                    .ConfigureAwait(false));

        app.MapGet(
            "/v1/facts",
            async (
                [FromQuery(Name = "as_of")] long? asOf,
                [FromQuery(Name = "version_id")] string? versionId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations
                    .SliceFactsAsync(asOf ?? 0, versionId ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false));

        app.MapPost(
            "/v1/context",
            async (
                WireContextRequest request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.ComposeContextAsync(request, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireComposedContext context => TypedResults.Json(context, WireJson.Default.WireComposedContext),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/search",
            async (
                WireSearchQuery query,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations.SearchAsync(query, cancellationToken).ConfigureAwait(false));

        app.MapPut(
            "/v1/sources",
            async (
                WireSourceIngest request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.IngestSourceAsync(request, cancellationToken).ConfigureAwait(false);

                // A document this port cannot read is 415 and one that is not the document declared is 422: a caller
                // has to be able to tell a wrong upload from an unreadable one, and a bare "bad request" cannot.
                IResult answer = result switch
                {
                    WireIngestedSource ingested => TypedResults.Json(
                        ingested, WireJson.Default.WireIngestedSource),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/sources/{source_id}",
            async (
                [FromRoute(Name = "source_id")] string sourceId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.GetSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireSourceInfo info => TypedResults.Json(info, WireJson.Default.WireSourceInfo),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/indexes",
            async (
                WireIndexBuild request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.BuildIndexVersionAsync(request, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireIndexVersion version => TypedResults.Json(version, WireJson.Default.WireIndexVersion),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/indexes/active",
            async (
                [FromQuery(Name = "collection_id")] string collectionId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .GetActiveIndexVersionAsync(collectionId, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireIndexVersion version => TypedResults.Json(version, WireJson.Default.WireIndexVersion),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/indexes/{index_version_id}",
            async (
                [FromRoute(Name = "index_version_id")] string indexVersionId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .GetIndexVersionAsync(indexVersionId, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireIndexVersion version => TypedResults.Json(version, WireJson.Default.WireIndexVersion),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/indexes/{index_version_id}/activate",
            async (
                [FromRoute(Name = "index_version_id")] string indexVersionId,
                WireIndexActivation activation,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .ActivateIndexVersionAsync(indexVersionId, activation.CollectionId, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireIndexVersion version => TypedResults.Json(version, WireJson.Default.WireIndexVersion),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/indexes/resolve",
            async (
                WireEnvelopeQuery query,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.ResolveEnvelopeAsync(query, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireEnvelopeResolution resolution => TypedResults.Json(
                        resolution, WireJson.Default.WireEnvelopeResolution),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/sources",
            async (
                [FromQuery(Name = "path_prefix")] string? pathPrefix,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations
                    .ListSourcesAsync(pathPrefix ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false));

        app.MapGet(
            "/v1/indexes",
            async (
                [FromQuery(Name = "collection_id")] string collectionId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations
                    .ListIndexVersionsAsync(collectionId, cancellationToken)
                    .ConfigureAwait(false));

        app.MapGet("/v1/shapes", (MunariumOperations operations) => operations.ListShapes());


        app.MapPost(
            "/v1/runbooks",
            async (
                WireRunbookApply request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.ApplyRunbookAsync(request, cancellationToken).ConfigureAwait(false);

                // An unreadable document is 400 and a removed version is 409: a caller has to be able to tell "your YAML
                // is wrong" from "that version is gone", and the problem's own status is what carries which.
                IResult answer = result switch
                {
                    WireAppliedRunbook applied => TypedResults.Json(applied, WireJson.Default.WireAppliedRunbook),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/runbooks",
            async (
                [FromQuery(Name = "include_removed")] bool? includeRemoved,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations
                    .ListRunbooksAsync(includeRemoved ?? false, cancellationToken)
                    .ConfigureAwait(false));

        app.MapPost(
            "/v1/runbooks/{name}/sessions",
            async (
                [FromRoute(Name = "name")] string name,
                HttpContext context,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                // The session plane carries the query scope, and the capability decides who the session belongs to: a
                // caller cannot open a conversation as somebody else by forgetting to say who it is.
                var access = MunariumKernel.Gate.Resolve(
                    context.Request.Headers.Authorization.ToString(),
                    AccessScope.Query,
                    DateTimeOffset.UtcNow);

                if (access is not EvidencePrincipal principal)
                {
                    return TypedResults.Json(
                        new WireProblem(
                            MunariumOperations.UnauthorizedProblem,
                            access is AccessRefused refused ? refused.Reason : AccessGate.MissingReason,
                            Status: 401,
                            ExpectedHead: 0,
                            ActualHead: 0),
                        WireJson.Default.WireProblem,
                        statusCode: 401);
                }

                var result = await operations
                    .CreateSessionAsync(name, principal, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireSessionCreated created => TypedResults.Json(created, WireJson.Default.WireSessionCreated),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/sessions/{session_id}/turns",
            async (
                [FromRoute(Name = "session_id")] string sessionId,
                WireTurnRequest request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .RunTurnAsync(sessionId, request, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireTurnResponse turn => TypedResults.Json(turn, WireJson.Default.WireTurnResponse),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        // The streamed turn: the same operation as the route above, with the kernel's progress reported as it happens.
        // The frame names are the original's - `progress`, then exactly one of `done` or `error` - and the terminal frame
        // carries what the unary route would have answered, because a stream that ends without saying how it ended is no
        // better than one that never started.
        app.MapPost(
            "/v1/sessions/{session_id}/turns/stream",
            async (
                [FromRoute(Name = "session_id")] string sessionId,
                WireTurnRequest request,
                HttpContext http,
                MunariumOperations operations) =>
            {
                // The turn runs beside the writer rather than before it, and the hand-off is an unbounded channel
                // because the alternative is a turn that stalls on a slow reader - which would make the time to the
                // first byte a function of the network rather than of the work. This is the original's own design: it
                // forwards progress events from a channel while the turn runs on its own task.
                var frames = Channel.CreateUnbounded<WireTurnEvent>();

                var running = Task.Run(async () =>
                {
                    // No cancellation token, deliberately: the turn's lifetime is the turn's and not the connection's.
                    // A conversation that stopped listening has already paid for this turn and still has to be able to
                    // say what it was told.
                    var result = await operations
                        .RunTurnAsync(sessionId, request, progress => frames.Writer.TryWrite(progress))
                        .ConfigureAwait(false);

                    frames.Writer.TryComplete();

                    return result;
                });

                http.Response.ContentType = "text/event-stream";

                // Nothing between this server and the client may hold the frames back: the original shipped a whole
                // event sequence in one burst at the end of a turn because a middleware buffered it, and the first byte
                // arrived after the work instead of during it.
                http.Response.Headers.CacheControl = "no-cache";
                http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

                await foreach (var progress in frames.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (!await TurnFrames.WriteFrameAsync(http.Response, "progress", TurnFrames.Json(progress))
                        .ConfigureAwait(false))
                    {
                        // The client hung up. The turn still finishes, because its record is what the next client reads.
                        await running.ConfigureAwait(false);

                        return;
                    }
                }

                var result = await running.ConfigureAwait(false);

                var (name, data) = result switch
                {
                    WireTurnResponse turn => ("done", JsonSerializer.Serialize(turn, WireJson.Default.WireTurnResponse)),
                    WireProblem problem => ("error", JsonSerializer.Serialize(problem, WireJson.Default.WireProblem)),
                };

                await TurnFrames.WriteFrameAsync(http.Response, name, data).ConfigureAwait(false);
            });

        app.MapGet(
            "/v1/sessions/{session_id}",
            async (
                [FromRoute(Name = "session_id")] string sessionId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireSession session => TypedResults.Json(session, WireJson.Default.WireSession),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/sessions/{session_id}/close",
            async (
                [FromRoute(Name = "session_id")] string sessionId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.CloseSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireSessionClosed closed => TypedResults.Json(closed, WireJson.Default.WireSessionClosed),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/evidence",
            async (
                WireSealEvidenceRequest request,
                HttpContext context,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var access = MunariumKernel.Gate.Resolve(
                    context.Request.Headers.Authorization.ToString(),
                    AccessScope.Evidence,
                    DateTimeOffset.UtcNow);

                if (access is not EvidencePrincipal principal)
                {
                    return TypedResults.Json(
                        new WireProblem(
                            MunariumOperations.UnauthorizedProblem,
                            access is AccessRefused refused ? refused.Reason : AccessGate.MissingReason,
                            Status: 401,
                            ExpectedHead: 0,
                            ActualHead: 0),
                        WireJson.Default.WireProblem,
                        statusCode: 401);
                }

                var result = await operations
                    .SealEvidenceAsync(request, principal, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireSealResponse sealed_ => TypedResults.Json(sealed_, WireJson.Default.WireSealResponse),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPut(
            "/v1/evidence/{evidence_id}/bytes",
            async (
                [FromRoute(Name = "evidence_id")] string evidenceId,
                [FromQuery(Name = "grant")] string grant,
                HttpContext context,
                WireEvidenceBytesUpload request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                // An empty grant is refused by the store as an unusable one, so a caller that forgets the query parameter
                // is told the grant is invalid rather than that its bytes were fine.
                if (!TryDecode(request.BytesBase64, out var bytes))
                {
                    return TypedResults.Json(
                        Invalid("bytes_base64 is not valid base64."),
                        WireJson.Default.WireProblem,
                        statusCode: 400);
                }

                var access = MunariumKernel.Gate.Resolve(
                    context.Request.Headers.Authorization.ToString(),
                    AccessScope.Evidence,
                    DateTimeOffset.UtcNow);

                if (access is not EvidencePrincipal principal)
                {
                    return TypedResults.Json(
                        new WireProblem(
                            MunariumOperations.UnauthorizedProblem,
                            access is AccessRefused refused ? refused.Reason : AccessGate.MissingReason,
                            Status: 401,
                            ExpectedHead: 0,
                            ActualHead: 0),
                        WireJson.Default.WireProblem,
                        statusCode: 401);
                }

                var refusal = await operations
                    .PutEvidenceBytesAsync(principal, evidenceId, grant ?? string.Empty, bytes, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = refusal is null
                    ? TypedResults.NoContent()
                    : TypedResults.Json(refusal, WireJson.Default.WireProblem, statusCode: refusal.Status);

                return answer;
            });

        app.MapPost(
            "/v1/evidence/{evidence_id}/commit",
            async (
                [FromRoute(Name = "evidence_id")] string evidenceId,
                HttpContext context,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var access = MunariumKernel.Gate.Resolve(
                    context.Request.Headers.Authorization.ToString(),
                    AccessScope.Evidence,
                    DateTimeOffset.UtcNow);

                if (access is not EvidencePrincipal principal)
                {
                    return TypedResults.Json(
                        new WireProblem(
                            MunariumOperations.UnauthorizedProblem,
                            access is AccessRefused refused ? refused.Reason : AccessGate.MissingReason,
                            Status: 401,
                            ExpectedHead: 0,
                            ActualHead: 0),
                        WireJson.Default.WireProblem,
                        statusCode: 401);
                }

                var result = await operations
                    .CommitEvidenceAsync(principal, evidenceId, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireEvidenceCommit committed => TypedResults.Json(
                        committed, WireJson.Default.WireEvidenceCommit),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/evidence/{evidence_id}",
            async (
                [FromRoute(Name = "evidence_id")] string evidenceId,
                HttpContext context,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var access = MunariumKernel.Gate.Resolve(
                    context.Request.Headers.Authorization.ToString(),
                    AccessScope.Evidence,
                    DateTimeOffset.UtcNow);

                if (access is not EvidencePrincipal principal)
                {
                    return TypedResults.Json(
                        new WireProblem(
                            MunariumOperations.UnauthorizedProblem,
                            access is AccessRefused refused ? refused.Reason : AccessGate.MissingReason,
                            Status: 401,
                            ExpectedHead: 0,
                            ActualHead: 0),
                        WireJson.Default.WireProblem,
                        statusCode: 401);
                }

                var result = await operations
                    .ReadEvidenceManifestAsync(principal, evidenceId, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    EvidenceManifest manifest => TypedResults.Json(manifest, WireJson.Default.EvidenceManifest),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/evidence/{evidence_id}/rows",
            async (
                [FromRoute(Name = "evidence_id")] string evidenceId,
                [FromQuery(Name = "from")] long? from,
                HttpContext context,
                [FromQuery(Name = "limit")] long? limit,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var access = MunariumKernel.Gate.Resolve(
                    context.Request.Headers.Authorization.ToString(),
                    AccessScope.Evidence,
                    DateTimeOffset.UtcNow);

                if (access is not EvidencePrincipal principal)
                {
                    return TypedResults.Json(
                        new WireProblem(
                            MunariumOperations.UnauthorizedProblem,
                            access is AccessRefused refused ? refused.Reason : AccessGate.MissingReason,
                            Status: 401,
                            ExpectedHead: 0,
                            ActualHead: 0),
                        WireJson.Default.WireProblem,
                        statusCode: 401);
                }

                var result = await operations
                    .ReadEvidenceRowsAsync(principal, evidenceId, from ?? 0, limit ?? 0, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireEvidenceRows rows => TypedResults.Json(rows, WireJson.Default.WireEvidenceRows),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapGet(
            "/v1/evidence/{evidence_id}/accesses",
            async (
                [FromRoute(Name = "evidence_id")] string evidenceId,
                [FromQuery(Name = "limit")] long? limit,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .ReadEvidenceAccessesAsync(MunariumKernel.Tenant, evidenceId, limit ?? 0, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireEvidenceAccessList accesses => TypedResults.Json(
                        accesses, WireJson.Default.WireEvidenceAccessList),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapDelete(
            "/v1/evidence/{evidence_id}",
            async (
                [FromRoute(Name = "evidence_id")] string evidenceId,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .PurgeEvidenceAsync(MunariumKernel.Tenant, evidenceId, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = result switch
                {
                    WireEvidencePurge purged => TypedResults.Json(purged, WireJson.Default.WireEvidencePurge),
                    WireProblem problem => TypedResults.Json(
                        problem, WireJson.Default.WireProblem, statusCode: problem.Status),
                };

                return answer;
            });

        app.MapPost(
            "/v1/evidence/{evidence_id}/legal-hold",
            async (
                [FromRoute(Name = "evidence_id")] string evidenceId,
                WireEvidenceLegalHold request,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var refusal = await operations
                    .SetEvidenceLegalHoldAsync(MunariumKernel.Tenant, evidenceId, request.Hold, cancellationToken)
                    .ConfigureAwait(false);

                IResult answer = refusal is null
                    ? TypedResults.NoContent()
                    : TypedResults.Json(refusal, WireJson.Default.WireProblem, statusCode: refusal.Status);

                return answer;
            });

        return app;
    }

    /// <summary>Decodes the base64 a JSON surface carries bytes in.</summary>
    private static bool TryDecode(string? base64, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrEmpty(base64))
        {
            return false;
        }

        var buffer = new byte[base64.Length];

        if (!Convert.TryFromBase64String(base64, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];

        return true;
    }

    private static WireProblem Invalid(string detail) =>
        new(MunariumOperations.InvalidRequestProblem, detail, Status: 400, ExpectedHead: 0, ActualHead: 0);
}
