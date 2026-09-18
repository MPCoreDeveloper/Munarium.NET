namespace Munarium.Providers;

using System.Text;
using Munarium.Evidence;
using Munarium.Ledger;

/// <summary>
/// Binds declared data views into the form the plane is called with.
/// </summary>
/// <remarks>
/// A profile declares a view by name and the client calls it by contract, so this is where the two meet - once, when
/// the profile is applied, and never per turn: a turn that could rebind a view would be choosing which contract to run,
/// which is the one thing the declaration exists to prevent.
/// </remarks>
public static class DataViewBindings
{
    /// <summary>Binds one declared view.</summary>
    /// <param name="declaration">The declaration.</param>
    /// <returns>The binding the provider consumes.</returns>
    public static BoundDataView ToBound(this DataViewDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        return new BoundDataView
        {
            Contract = declaration.Contract,
            Kind = declaration.Kind,
            ParametersJson = ParametersOf(declaration),
            AccessLevel = declaration.AccessLevel,
            Compartments = declaration.Compartments,
        };
    }

    /// <summary>Binds every declared view, keyed by the name a layer pins.</summary>
    /// <param name="declarations">The declarations.</param>
    /// <returns>The views, by name.</returns>
    public static IReadOnlyDictionary<string, BoundDataView> ToBoundViews(
        this IReadOnlyList<DataViewDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        return declarations.ToDictionary(
            declaration => declaration.Name,
            declaration => declaration.ToBound(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes the declared parameters as the JSON object a request carries.
    /// </summary>
    /// <remarks>
    /// Every value is written as text, whatever its declared type: a decimal parameter that round-tripped through a
    /// number arrives at the source having lost the precision the contract was written to keep. Names are sorted, so the
    /// same declaration is the same bytes and a diff of two requests means something.
    /// </remarks>
    private static string ParametersOf(DataViewDeclaration declaration) =>
        declaration.Parameters.Count == 0
            ? "{}"
            : Encoding.UTF8.GetString(PayloadJson.Write(writer =>
            {
                writer.WriteStartObject();

                foreach (var (name, parameter) in declaration.Parameters.OrderBy(
                    pair => pair.Key,
                    StringComparer.Ordinal))
                {
                    writer.WriteStartObject(name);
                    writer.WriteString("type", parameter.Type);
                    writer.WriteString("value", parameter.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }));
}
