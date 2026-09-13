namespace Munarium.Server.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

/// <summary>
/// Hosts the real server in process: the same <c>Program</c> a deployment runs, over a database of
/// its own and the shapes this repository ships.
/// </summary>
/// <remarks>
/// Nothing about the application is stubbed, so these tests hold the composition as well as the
/// endpoints - a kernel that cannot be composed fails here rather than at deploy time.
/// </remarks>
public sealed class MunariumApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"Munarium_{Guid.NewGuid():N}");

    /// <summary>Gets the shapes directory the application under test is pointed at.</summary>
    public static string ShapesDirectory => Path.Combine(AppContext.BaseDirectory, "shapes");

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("Munarium:DatabasePath", _databasePath);
        builder.UseSetting("Munarium:DatabaseName", "munarium-tests");
        builder.UseSetting("Munarium:ShapesDirectory", ShapesDirectory);
    }
}
