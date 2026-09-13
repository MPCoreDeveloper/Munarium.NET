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

        app.MapGet(
            "/v1/streams/{stream}/head",
            async (
                string stream,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations.GetHeadAsync(stream, cancellationToken).ConfigureAwait(false));

        app.MapPost(
            "/v1/streams/{stream}/claims",
            async (
                string stream,
                WireClaimProposal proposal,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations
                    .ProposeClaimAsync(stream, proposal, cancellationToken)
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

        app.MapGet(
            "/v1/facts",
            async (
                [FromQuery(Name = "as_of")] long? asOf,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations.SliceFactsAsync(asOf ?? 0, cancellationToken).ConfigureAwait(false));

        app.MapPost(
            "/v1/search",
            async (
                WireSearchQuery query,
                MunariumOperations operations,
                CancellationToken cancellationToken) =>
                await operations.SearchAsync(query, cancellationToken).ConfigureAwait(false));

        app.MapGet("/v1/shapes", (MunariumOperations operations) => operations.ListShapes());

        return app;
    }
}
