namespace Munarium.Server;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
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
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .ReleaseAnchorAsync(versionId, detailKey, cancellationToken)
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
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .FulfilPromiseAsync(versionId, key, cancellationToken)
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


        return app;
    }
}
