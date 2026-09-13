namespace Munarium.Core.Tests.Shapes;

using Munarium.Core.Tests.Support;
using Munarium.Shapes;

/// <summary>
/// Tests for shape resolution and for the lineage a shape declares, which is what decides
/// supersession in the fact ledger.
/// </summary>
public class ShapeRegistryTests
{
    [Fact]
    public void ARegisteredShapeResolvesByName() =>
        Assert.Equal("vendor", VendorShape.Registry().Resolve("vendor").Name);

    [Fact]
    public void ResolvingAnUnregisteredShapeFails() =>
        Assert.Throws<KeyNotFoundException>(() => VendorShape.Registry().Resolve("patent"));

    [Fact]
    public void TryResolveReportsAMissRatherThanThrowing()
    {
        Assert.False(VendorShape.Registry().TryResolve("patent", out var shape));
        Assert.Null(shape);
    }

    [Fact]
    public void RegisteringTheSameNameTwiceIsRefused()
    {
        var shape = VendorShape.Create();

        Assert.Throws<ArgumentException>(() => new ShapeRegistry([shape, shape]));
    }

    [Fact]
    public void TheLineageIsTheIdentityFieldsInDeclarationOrder()
    {
        var shape = new FactShape
        {
            Name = "contract",
            Version = 3,
            Identity = ["contract_id", "clause"],
            Schema = """{"type":"object"}""",
        };

        // The body lists the fields the other way round: declaration order wins, not document order.
        Assert.Equal(
            "contract@3|contract_id=C-1|clause=termination",
            shape.LineageOf("""{"clause":"termination","contract_id":"C-1"}"""));
    }

    [Fact]
    public void ANonStringIdentityValueIsRenderedCanonically() =>
        Assert.Equal("vendor@1|vendor_id=42", VendorShape.Create().LineageOf("""{"vendor_id":42}"""));

    [Fact]
    public void AMissingIdentityFieldYieldsAStableLineageRatherThanFailing()
    {
        var shape = VendorShape.Create();

        // This is what lets a malformed claim still be recorded as disputed instead of being
        // unrecordable.
        Assert.Equal("vendor@1|vendor_id=", shape.LineageOf("{}"));
        Assert.Equal("vendor@1|vendor_id=", shape.LineageOf(null));
    }

    [Fact]
    public void AnUnregisteredShapeStillYieldsALineage() =>
        Assert.Equal("patent@unregistered", VendorShape.Registry().LineageOf("patent", """{"patent_id":"P-1"}"""));

    [Fact]
    public void TwoVersionsOfAShapeDoNotSupersedeEachOther()
    {
        var first = VendorShape.Create();
        var second = first with { Version = 2 };
        var body = VendorShape.Body("north");

        Assert.NotEqual(first.LineageOf(body), second.LineageOf(body));
    }
}
