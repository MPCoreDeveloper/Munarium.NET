namespace Munarium.Providers.Tests;

using System.Net;
using System.Text.Json;
using Munarium.Evidence;

/// <summary>
/// What a data view is asked for.
/// </summary>
/// <remarks>
/// The turn's question is deliberately not sent, and these tests hold that: what crosses the boundary is a contract
/// name with typed parameters, or names drawn from lists the view declares. Never SQL.
/// </remarks>
public class MatrixIntentTests
{
    [Fact]
    public void ASemanticViewPostsNamesNeverSqlAndBindsFiltersByType()
    {
        var view = new BoundDataView
        {
            Contract = "pipeline-by-region",
            Kind = DataViewKind.DataView,

            // The view's ceiling, below the session's, so the lower of the two has to win.
            AccessLevel = 0,
        };

        var selection = new SemanticSelection
        {
            Measures = ["pipeline_amount"],
            Dimensions = ["region"],
            Filters = [new SemanticFilterSelection("stage", "Proposal")],
        };

        using var body = Body(MatrixIntent.BodyOf(view, selection, Authorization()));

        Assert.Equal("semantic", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal("pipeline-by-region", body.RootElement.GetProperty("semantic").GetProperty("provider").GetString());

        var filter = Assert.Single(body.RootElement.GetProperty("semantic").GetProperty("filters").EnumerateArray());

        Assert.Equal("eq", filter.GetProperty("op").GetString());
        Assert.Equal("string", filter.GetProperty("value").GetProperty("type").GetString());
        Assert.Equal("Proposal", filter.GetProperty("value").GetProperty("value").GetString());

        Assert.Equal(0, body.RootElement.GetProperty("authorization").GetProperty("access_level").GetInt32());

        // No contract name: a semantic view is asked with names, and carrying the contract would invite a caller to
        // think it chose one.
        Assert.False(body.RootElement.TryGetProperty("contract", out _));
    }

    [Fact]
    public void AContractViewPostsTheStructuredQueryExactlyAsBefore()
    {
        var view = new BoundDataView
        {
            Contract = "open-pipeline-by-region@3",
            Kind = DataViewKind.Contract,
            ParametersJson = """{"as_of":{"type":"date","value":"2026-06-30"}}""",
            AccessLevel = 3,
        };

        using var body = Body(MatrixIntent.BodyOf(view, null, Authorization()));

        Assert.Equal("structured_query", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal("open-pipeline-by-region@3", body.RootElement.GetProperty("contract").GetString());
        Assert.Equal(
            "2026-06-30",
            body.RootElement.GetProperty("parameters").GetProperty("as_of").GetProperty("value").GetString());
    }

    [Fact]
    public void TheCompartmentsAreTheIntersectionOfTheSessionAndTheView()
    {
        var view = new BoundDataView
        {
            Contract = "open-pipeline-by-region@3",
            Kind = DataViewKind.Contract,
            AccessLevel = 3,
            Compartments = ["sales"],
        };

        using var body = Body(MatrixIntent.BodyOf(view, null, Authorization()));

        var compartments = body.RootElement
            .GetProperty("authorization")
            .GetProperty("compartments")
            .EnumerateArray()
            .Select(value => value.GetString());

        // The session holds sales and finance; the view was declared for sales. Sending finance would be reaching past
        // what the view was declared for, which is the higher of the two and never the right one.
        Assert.Equal(["sales"], compartments);
    }

    private static SessionAuthorization Authorization() => new()
    {
        Tenant = "demo",
        Uid = "u",
        AccessLevel = 3,
        Compartments = ["finance", "sales"],
        SessionId = "s",
        RunbookRef = "r@1",
    };

    private static JsonDocument Body(byte[] body) => JsonDocument.Parse(body);
}
