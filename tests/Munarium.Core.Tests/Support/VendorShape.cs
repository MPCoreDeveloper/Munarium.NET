namespace Munarium.Core.Tests.Support;

using Munarium.Shapes;

/// <summary>
/// The shape the kernel tests claim under: a vendor with an identifier and a status.
/// </summary>
/// <remarks>
/// It is deliberately small but non-trivial - a required field, an enum, a length bound, and one
/// identity field - so the schema, the lineage and the gate are all exercised by it.
/// </remarks>
internal static class VendorShape
{
    /// <summary>The shape name.</summary>
    internal const string Name = "vendor";

    /// <summary>The schema a vendor fact body must satisfy.</summary>
    internal const string Schema = """
        {
          "type": "object",
          "required": ["vendor_id", "status"],
          "additionalProperties": false,
          "properties": {
            "vendor_id": { "type": "string", "minLength": 1 },
            "status": { "type": "string", "enum": ["approved", "pending", "sanctioned"] }
          }
        }
        """;

    /// <summary>Builds the shape.</summary>
    /// <returns>The vendor shape.</returns>
    internal static FactShape Create() => new()
    {
        Name = Name,
        Version = 1,
        Identity = ["vendor_id"],
        Schema = Schema,
    };

    /// <summary>Builds a registry containing the vendor shape.</summary>
    /// <returns>The registry.</returns>
    internal static ShapeRegistry Registry() => new([Create()]);

    /// <summary>Builds a well formed vendor body.</summary>
    /// <param name="vendorId">The vendor identifier, which is also the lineage.</param>
    /// <param name="status">The vendor status.</param>
    /// <returns>The body as JSON.</returns>
    internal static string Body(string vendorId, string status = "approved") =>
        $$"""{"vendor_id":"{{vendorId}}","status":"{{status}}"}""";

    /// <summary>Builds the lineage this shape derives for a vendor.</summary>
    /// <param name="vendorId">The vendor identifier.</param>
    /// <returns>The lineage.</returns>
    internal static string Lineage(string vendorId) => $"vendor@1|vendor_id={vendorId}";
}
