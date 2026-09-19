namespace Munarium.Runbooks.Tests.Support;

/// <summary>
/// The worked example the original ships, found from wherever the test binary happens to live.
/// </summary>
/// <remarks>
/// The conformance fixture is the original's own document byte for byte, and both suites that read it resolve it the
/// same way: by walking up to the repository root, so a test never depends on the output directory's depth.
/// </remarks>
internal static class Examples
{
    /// <summary>Gets the directory the example lives in.</summary>
    public static string Directory { get; } = Find();

    /// <summary>Reads the worked example.</summary>
    /// <returns>The YAML, as written.</returns>
    public static string Runbook() => File.ReadAllText(Path.Combine(Directory, "runbook.yaml"));

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "contract", "lab", "example");

            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("contract/lab/example was not found above the test binary.");
    }
}
