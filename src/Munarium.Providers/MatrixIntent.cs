namespace Munarium.Providers;

using System.Text.Json;
using Munarium.Evidence;
using Munarium.Ledger;

/// <summary>
/// What a data view is asked for.
/// </summary>
/// <remarks>
/// The turn's question is deliberately not sent. Matrix executes a pre-declared contract with typed parameters, and a
/// metric view or native data view is answered from names drawn from lists it declares: the contract's own schema says
/// no free-form expression crosses this boundary, and that is the property that makes an injection structurally
/// impossible here rather than merely defended against.
/// </remarks>
public static class MatrixIntent
{
    /// <summary>How many rows a request asks for at most.</summary>
    public const int MaxRows = 500;

    /// <summary>How many bytes a request asks for at most.</summary>
    public const long MaxBytes = 1_048_576;

    /// <summary>
    /// Builds the body a view is executed with.
    /// </summary>
    /// <param name="view">The bound view.</param>
    /// <param name="selection">What the intent task chose, for a semantic view.</param>
    /// <param name="authorization">The session's own authorization.</param>
    /// <returns>The body, as UTF-8 JSON.</returns>
    public static byte[] BodyOf(
        BoundDataView view,
        SemanticSelection? selection,
        SessionAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(authorization);

        return PayloadJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("contract_version", EvidenceContract.Version);

            if (selection is { } chosen)
            {
                writer.WriteString("kind", "semantic");
                writer.WriteStartObject("semantic");
                writer.WriteString("provider", view.Contract);
                WriteStrings(writer, "measures", chosen.Measures);
                WriteStrings(writer, "dimensions", chosen.Dimensions);
                writer.WriteStartArray("filters");

                foreach (var filter in chosen.Filters)
                {
                    writer.WriteStartObject();
                    writer.WriteString("dimension", filter.Dimension);
                    writer.WriteString("op", "eq");
                    writer.WriteStartObject("value");

                    // The declared type travels with the value so it is bound as that type rather than as text, which
                    // is what keeps a date a date and a number a number on the far side.
                    writer.WriteString("type", filter.Type);
                    writer.WriteString("value", filter.Value);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                WriteAuthorization(writer, view, authorization);
                WriteLimits(writer);
                writer.WriteStartObject("parameters");
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteString("kind", "structured_query");
                writer.WriteString("contract", view.Contract);
                WriteAuthorization(writer, view, authorization);
                WriteLimits(writer);

                // The profile's parameters, verbatim: the kernel has no vocabulary to check them against, and the
                // contract's schema is what says whether one is bound.
                writer.WritePropertyName("parameters");
                writer.WriteRawValue(view.ParametersJson);
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Writes the authorization the plane re-checks.
    /// </summary>
    /// <remarks>
    /// The access level is the <em>lower</em> of what the session holds and what the view demands, and the compartments
    /// are the intersection of the two. A layer therefore cannot borrow clearance the session does not have, and a
    /// session cannot reach past what the view was declared for.
    /// </remarks>
    private static void WriteAuthorization(
        Utf8JsonWriter writer,
        BoundDataView view,
        SessionAuthorization authorization)
    {
        writer.WriteStartObject("authorization");
        writer.WriteString("tenant", authorization.Tenant);
        writer.WriteString("uid", authorization.Uid);
        writer.WriteNumber("access_level", Math.Min(authorization.AccessLevel, view.AccessLevel));
        writer.WriteStartArray("compartments");

        foreach (var compartment in authorization.Compartments.Where(compartment =>
            view.Compartments.Count == 0 || view.Compartments.Contains(compartment, StringComparer.Ordinal)))
        {
            writer.WriteStringValue(compartment);
        }

        writer.WriteEndArray();
        writer.WriteString("session_id", authorization.SessionId);
        writer.WriteString("runbook_ref", authorization.RunbookRef);
        writer.WriteEndObject();
    }

    private static void WriteLimits(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("limits");
        writer.WriteNumber("max_rows", MaxRows);
        writer.WriteNumber("max_bytes", MaxBytes);
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);

        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
