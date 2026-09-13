namespace Munarium.Server.Tests;

using Munarium.Server;
using Munarium.Shapes;

/// <summary>
/// Shapes are deployment data rather than code, so loading them is a surface worth holding still.
/// </summary>
public class MunariumShapeBundlesTests
{
    [Fact]
    public void AShapeIsReadFromItsOnDiskForm()
    {
        var shape = MunariumShapeBundles.Read("""
            {
              "name": "vendor",
              "version": 2,
              "identity": ["vendor_id"],
              "schema": { "type": "object", "required": ["vendor_id"] }
            }
            """);

        Assert.Equal("vendor", shape.Name);
        Assert.Equal(2, shape.Version);
        Assert.Equal(["vendor_id"], shape.Identity);
        Assert.Contains("\"required\"", shape.Schema, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnconfiguredDirectoryIsAnEmptyRegistryRatherThanAFailure()
    {
        Assert.Equal(0, MunariumShapeBundles.Load(null).Count);
        Assert.Equal(0, MunariumShapeBundles.Load(Path.Combine(Path.GetTempPath(), "no-such-shapes")).Count);
    }

    [Fact]
    public void TheShippedShapesAreWellFormed()
    {
        var vendor = MunariumShapeBundles.Load(MunariumApiFactory.ShapesDirectory).Resolve("vendor");

        Assert.True(FactSchemaValidator
            .Validate(vendor.Schema, """{"vendor_id":"v-1","status":"approved"}""")
            .Valid);

        Assert.False(FactSchemaValidator
            .Validate(vendor.Schema, """{"vendor_id":"v-1","status":"unknown"}""")
            .Valid);
    }
}
