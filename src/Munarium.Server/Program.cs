// The Munarium server: the JSON/HTTP surface of the wire contract.
//
// The contract itself is openapi/munarium.v1.yaml, and it is the same specification the
// gRPC/protobuf surface is generated from - so a change to the contract is a change to one file.

using Munarium.Server;
using Munarium.Wire;
using System.Text.Json;

var builder = WebApplication.CreateSlimBuilder(args);

// The contract's own JSON: snake_case field names, and source-generated metadata so a published
// native binary needs no reflection. The resolver chain supplies the metadata, but the naming policy
// has to be set on these options too - an instance-level policy wins over the context's, and the
// framework's web defaults are camelCase.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, WireJson.Default);
});

// The gRPC surface of the same contract: the service base SharpPortico generated from
// openapi/munarium.v1.yaml is served here, over the same MunariumOperations the JSON surface uses.
builder.Services.AddGrpc();

var shapes = MunariumShapeBundles.Load(
    builder.Configuration["Munarium:ShapesDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "shapes"));

var kernel = MunariumKernel.Create(
    builder.Configuration["Munarium:DatabasePath"] ?? Path.Combine(Path.GetTempPath(), "munarium"),
    builder.Configuration["Munarium:DatabaseName"] ?? "munarium",
    shapes);

builder.Services.AddSingleton(kernel);
builder.Services.AddSingleton(kernel.Operations);
builder.Services.AddSingleton<MunariumGrpcService>();

var app = builder.Build();

app.MapMunarium();
app.MapGrpcService<MunariumGrpcService>();

await app.RunAsync();

/// <summary>
/// The server's entry point, named so a test can host this application in process.
/// </summary>
public partial class Program
{
    /// <summary>Prevents instantiation; the host calls the generated entry point.</summary>
    protected Program()
    {
    }
}
