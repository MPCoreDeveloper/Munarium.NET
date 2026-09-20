namespace Munarium.Server.Tests;

/// <summary>Tests for how this port's contract relates to the original's.</summary>
/// <remarks>
/// <c>contract/upstream/mmp-v1-operations.txt</c> is generated from the original's own OpenAPI document by
/// <c>tools/spec-coverage.ps1</c>, and this test holds two things to it: no operation this port defines is invented, and
/// the number it covers never falls. An operation added later that is neither upstream's nor listed as an extension fails
/// here - which is the only way a divergence stays a decision rather than an accident.
/// </remarks>
public class UpstreamContractTests
{
    /// <summary>The operations this port defines that the original does not, each grouped by why it exists.</summary>
    private static readonly string[] Extensions =
    [
        // The reads: this port serves them as GETs, where the original reads through a POST body or an operation that
        // carries its filters as arguments. The same path is not the same operation.
        "GET /v1/facts",
        "GET /v1/indexes",
        "GET /v1/indexes/{index_version_id}",
        "GET /v1/indexes/active",
        "GET /v1/sessions/{session_id}",
        "GET /v1/shapes",
        "GET /v1/snapshots",
        "GET /v1/sources",

        // The index routes: the original names a version through an artifact plane and builds per shape ref, while this
        // port builds and activates a version by collection id.
        "POST /v1/indexes",
        "POST /v1/indexes/{index_version_id}/activate",
        "POST /v1/indexes/resolve",

        // The context and session routes: composed context and turn-taking over one route each, where the original splits
        // them across sessions, runs and profiles.
        "POST /v1/context",
        "POST /v1/sessions/{session_id}/close",
        "POST /v1/sessions/{session_id}/turns",
        "POST /v1/sessions/{session_id}/turns/stream",

        // The keyed planes an executor drives, which this port serves under its own names.
        "POST /v1/versions/{version_id}/anchors/{detail_key}/release",
        "POST /v1/versions/{version_id}/claim-batches",
    ];

    /// <summary>Every operation this port defines is upstream's or a recorded extension.</summary>
    [Fact]
    public void EveryOperationIsEitherUpstreamsOrARecordedExtension()
    {
        var declared = Upstream();

        var invented = Served()
            .Where(operation => !declared.Contains(operation) && !Extensions.Contains(operation, StringComparer.Ordinal))
            .ToArray();

        Assert.Empty(invented);
    }

    /// <summary>The number of upstream operations this port covers never falls.</summary>
    /// <remarks>
    /// A floor rather than an equality: it rises as operations are ported and has to be raised here in the same commit,
    /// which is the moment to say what was ported and what is still absent.
    /// </remarks>
    [Fact]
    public void CoverageNeverFalls()
    {
        var declared = Upstream();
        var covered = Served().Count(declared.Contains);

        Assert.True(covered >= 37, $"this port covers {covered} of the original's operations, and it covered 37 before");
    }

    /// <summary>Reads the recorded upstream operations.</summary>
    /// <returns>The operations, as METHOD and path.</returns>
    private static HashSet<string> Upstream() =>
        [.. File
            .ReadAllLines(At("contract/upstream/mmp-v1-operations.txt"))
            .Where(line => line.Trim().Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Trim())];

    /// <summary>Reads the operations this port's own specification defines.</summary>
    /// <returns>The operations, as METHOD and path.</returns>
    private static string[] Served()
    {
        var served = new List<string>();
        var current = string.Empty;

        foreach (var line in File.ReadAllLines(At("openapi/munarium.v1.yaml")))
        {
            if (line.StartsWith("  /v1/", StringComparison.Ordinal) && line.EndsWith(':'))
            {
                current = line.Trim().TrimEnd(':');
            }
            else if (current.Length > 0 && line.StartsWith("    ", StringComparison.Ordinal) && line.EndsWith(':'))
            {
                var method = line.Trim().TrimEnd(':').ToUpperInvariant();

                if (method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH")
                {
                    served.Add($"{method} {current}");
                }
            }
        }

        return [.. served.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>Finds a path in this repository from wherever the test binary happens to live.</summary>
    /// <param name="relative">The path to find.</param>
    /// <returns>The absolute path.</returns>
    private static string At(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"'{relative}' was not found above the test binary.");
    }
}
