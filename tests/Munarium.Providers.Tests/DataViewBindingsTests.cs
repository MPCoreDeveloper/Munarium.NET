namespace Munarium.Providers.Tests;

using System.Text.Json;
using Munarium.Evidence;

/// <summary>
/// Declared data views, bound for the plane.
/// </summary>
/// <remarks>
/// The binding happens once, when a profile is applied, so these tests hold what the provider will then be handed: a
/// contract to call, a kind that decides the request's shape, and parameters that are already text.
/// </remarks>
public class DataViewBindingsTests
{
    [Fact]
    public void ADeclarationBindsItsContractKindAndCeiling()
    {
        var declaration = new DataViewDeclaration
        {
            Name = "revenue_by_region",
            Contract = "open-pipeline-by-region@2",
            Kind = DataViewKind.Contract,
            AccessLevel = 2,
            Compartments = ["sales"],
        };

        var bound = declaration.ToBound();

        Assert.Equal("open-pipeline-by-region@2", bound.Contract);
        Assert.Equal(DataViewKind.Contract, bound.Kind);
        Assert.Equal(2, bound.AccessLevel);
        Assert.Equal(["sales"], bound.Compartments);
        Assert.Equal("{}", bound.ParametersJson);
    }

    [Fact]
    public void ParametersAreBoundAsTextAndInSortedOrder()
    {
        var declaration = new DataViewDeclaration
        {
            Name = "revenue",
            Contract = "open-pipeline@3",
            Parameters = new Dictionary<string, DataViewParameter>(StringComparer.Ordinal)
            {
                ["as_of"] = new() { Type = "date", Value = "2026-06-30" },
                ["amount"] = new() { Type = "decimal", Value = "900000.50" },
            },
        };

        using var parameters = JsonDocument.Parse(declaration.ToBound().ParametersJson);

        // Text, never a number: a decimal that round-tripped through a double arrives having lost the precision the
        // contract was written to keep.
        Assert.Equal(
            "900000.50",
            parameters.RootElement.GetProperty("amount").GetProperty("value").GetString());
        Assert.Equal("decimal", parameters.RootElement.GetProperty("amount").GetProperty("type").GetString());

        // Sorted, so the same declaration is the same bytes and a diff of two requests means something.
        Assert.Equal(
            ["amount", "as_of"],
            parameters.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void DeclarationsBindIntoTheViewsAProviderServes()
    {
        var views = new[]
        {
            new DataViewDeclaration { Name = "revenue", Contract = "open-pipeline@3" },
        }.ToBoundViews();

        var bound = Assert.Contains("revenue", views);

        Assert.Equal("open-pipeline@3", bound.Contract);

        // The dictionary is keyed by the name a layer pins, without the prefix: the prefix is how the layer spells the
        // plane, and it never reaches the plane.
        Assert.False(views.ContainsKey("matrix:revenue"));
    }
}
