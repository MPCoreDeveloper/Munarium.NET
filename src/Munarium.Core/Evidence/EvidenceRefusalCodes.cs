namespace Munarium.Evidence;

/// <summary>
/// The refusal codes a layer can report.
/// </summary>
/// <remarks>
/// Kebab-case, matching the server's problem registry: a refusal is a thing a caller may see, so its code is part
/// of the contract rather than a log line.
/// </remarks>
public static class EvidenceRefusalCodes
{
    /// <summary>The source exists but could not be reached.</summary>
    public const string SourceUnavailable = "source-unavailable";

    /// <summary>The source did not answer in time.</summary>
    public const string SourceTimeout = "source-timeout";

    /// <summary>The source's circuit breaker is open, so it was not tried.</summary>
    public const string SourceCircuitOpen = "source-circuit-open";

    /// <summary>No provider is bound to the layer's sources.</summary>
    public const string SourceNotBound = "source-not-bound";

    /// <summary>The source rejected the request.</summary>
    public const string SourceRequestRejected = "source-request-rejected";

    /// <summary>The turn could not be turned into a question the plane could be asked.</summary>
    public const string IntentUnresolved = "intent-unresolved";

    /// <summary>The code reported when a layer failed without naming why.</summary>
    public const string Unavailable = "unavailable";
}
