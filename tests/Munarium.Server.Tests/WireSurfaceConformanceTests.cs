namespace Munarium.Server.Tests;

using System.Reflection;
using System.Text;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Munarium.Wire.Generated;

/// <summary>
/// The contract and the code agree, in the two places where they can drift apart without saying so.
/// </summary>
/// <remarks>
/// A surface that answers <c>UNIMPLEMENTED</c> because nobody wrote the adapter looks exactly like a surface whose
/// operation is not meant to be served over that transport, and a path the contract declares with no route registered
/// looks like a typo in a client's URL. Both failures are silent, so both are checked here rather than when a caller
/// trips over them.
/// </remarks>
public class WireSurfaceConformanceTests(MunariumApiFactory factory) : IClassFixture<MunariumApiFactory>
{
    private readonly MunariumApiFactory _factory = factory;

    /// <summary>Every operation the document declares is answered by this repository, not by the base class' default.</summary>
    [Fact]
    public void EveryOperationTheContractDeclaresHasAnAdapter()
    {
        var service = Type.GetType("Munarium.Server.MunariumGrpcService, Munarium.Server", throwOnError: false);

        Assert.NotNull(service);

        var unanswered = new List<string>();

        foreach (var operation in DeclaredOperations())
        {
            var adapter = service.GetMethod(
                operation.Name,
                BindingFlags.Public | BindingFlags.Instance,
                [.. operation.GetParameters().Select(parameter => parameter.ParameterType)]);

            // Declaring it is what matters: the generated base answers every operation with UNIMPLEMENTED, so an
            // operation that exists but is not overridden is a call nobody serves and nothing else would notice.
            if (adapter is null || adapter.DeclaringType != service)
            {
                unanswered.Add(operation.Name);
            }
        }

        Assert.Empty(unanswered);
    }

    /// <summary>Every path and verb the document declares is routed by the application that serves it.</summary>
    [Fact]
    public void EveryRouteTheContractDeclaresIsServed()
    {
        var served = _factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                Pattern: Normalize(endpoint.RoutePattern.RawText ?? string.Empty),
                Methods: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .ToList();

        Assert.NotEmpty(served);

        var unrouted = DeclaredRoutes()
            .Where(route => !served.Any(servedRoute =>
                string.Equals(servedRoute.Pattern, route.Pattern, StringComparison.Ordinal)
                    && servedRoute.Methods.Contains(route.Method, StringComparer.OrdinalIgnoreCase)))
            .Select(route => $"{route.Method} {route.Pattern}")
            .ToList();

        Assert.Empty(unrouted);
    }

    /// <summary>Gets the operations the generated service declares, once per operation rather than once per overload.</summary>
    private static IEnumerable<MethodInfo> DeclaredOperations()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declared in typeof(MunariumServiceBase).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!declared.IsVirtual
                || !declared.Name.EndsWith("Async", StringComparison.Ordinal)
                || !declared.GetParameters().Any(parameter => parameter.ParameterType == typeof(ServerCallContext))
                || !seen.Add(declared.Name))
            {
                continue;
            }

            yield return declared;
        }
    }

    /// <summary>Gets the paths and verbs the document declares.</summary>
    /// <remarks>
    /// Read as text rather than through a YAML parser, because the only thing needed from it is the pair of indentation
    /// levels that declare a route, and a parser would be a dependency this repository does not otherwise carry.
    /// </remarks>
    private static List<(string Method, string Pattern)> DeclaredRoutes()
    {
        var routes = new List<(string Method, string Pattern)>();
        var inPaths = false;
        string? path = null;

        foreach (var line in File.ReadAllLines(ContractPath()))
        {
            if (line.StartsWith("paths:", StringComparison.Ordinal))
            {
                inPaths = true;
                continue;
            }

            if (!inPaths)
            {
                continue;
            }

            if (line.StartsWith("components:", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("  /", StringComparison.Ordinal))
            {
                path = Normalize(line[2..].TrimEnd(':'));
                continue;
            }

            var trimmed = line.TrimStart();

            // A verb is a bare key under a path. Nothing else inside the paths section is one, which is why the text is
            // compared rather than the indentation: the indentation is a style the document is free to change.
            if (path is null)
            {
                continue;
            }

            foreach (var method in (string[])["get", "put", "post", "delete", "patch"])
            {
                if (trimmed.Equals($"{method}:", StringComparison.Ordinal))
                {
                    routes.Add((method.ToUpperInvariant(), path));
                }
            }
        }

        Assert.NotEmpty(routes);

        return routes;
    }

    /// <summary>Finds the contract document by walking up from the test binary to the repository root.</summary>
    private static string ContractPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "openapi", "munarium.v1.yaml");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("openapi/munarium.v1.yaml was not found above the test binary.");
    }

    /// <summary>
    /// Reduces a route to its shape, so a parameter's name need not match between the contract and the application.
    /// </summary>
    /// <remarks>
    /// Parameters are dropped rather than compared, because <c>{evidence_id}</c> in the contract and <c>{id}</c> in the
    /// route are the same route: what a caller has to be able to construct is the shape.
    /// </remarks>
    private static string Normalize(string pattern)
    {
        var normalized = new StringBuilder(pattern.Length);
        var inParameter = false;

        foreach (var character in pattern)
        {
            switch (character)
            {
                case '{':
                    inParameter = true;
                    normalized.Append("{}");
                    break;

                case '}':
                    inParameter = false;
                    break;

                default:
                    if (!inParameter)
                    {
                        normalized.Append(character);
                    }

                    break;
            }
        }

        return normalized.ToString();
    }
}
