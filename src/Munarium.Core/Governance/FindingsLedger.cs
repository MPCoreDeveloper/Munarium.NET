namespace Munarium.Governance;

using Munarium.Ledger;

/// <summary>
/// Reads a version's findings back out of the ledger.
/// </summary>
/// <remarks>
/// This is the port's answer to the original's findings table: the findings are recorded in the stream of
/// the version they were computed for, so they live under the same pin as the claims they judge and there
/// is no second store that can be out of step with the first. A query is therefore a read of that stream
/// rather than a table scan, which is the honest trade: the findings of one version are a short list, and
/// what a caller wants from them is "what was decided, at which position" - not an index.
/// </remarks>
/// <param name="storage">The ledger's storage seam.</param>
public sealed class FindingsLedger(IStorageBackend storage)
{
    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    /// <summary>
    /// Reads a version's recorded findings.
    /// </summary>
    /// <param name="versionId">The version whose stream is read.</param>
    /// <param name="query">The filter; <see langword="null"/> reads every finding the version holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The findings, oldest first.</returns>
    public async ValueTask<IReadOnlyList<StoredFinding>> ReadAsync(
        string versionId,
        FindingsQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var request = query ?? new FindingsQuery();
        var entries = await _storage
            .ReadAsync(StreamId.From(versionId), SequenceNumber.Zero, cancellationToken)
            .ConfigureAwait(false);

        var recorded = entries
            .Where(entry => FindingCodec.IsFindingsEvent(entry.Event.Type))
            .SelectMany(entry => FindingCodec
                .Decode(entry.Event.Payload.Span)
                .Where(finding => Selects(request, entry.Sequence, finding))
                .Select(finding => new StoredFinding(entry.Sequence, finding)))
            .ToList();

        return request.Limit is { } limit && recorded.Count > limit
            ? [.. recorded.Take(limit)]
            : recorded;
    }

    private static bool Selects(FindingsQuery query, SequenceNumber sequence, Claims.GateFinding finding) =>
        InRange(query, sequence)
        && MatchesSeverity(query, finding)
        && MatchesRule(query, finding);

    private static bool InRange(FindingsQuery query, SequenceNumber sequence) =>
        query.AsOfSequence is not { } pin || sequence <= pin;

    private static bool MatchesSeverity(FindingsQuery query, Claims.GateFinding finding) =>
        query.Severity is not { } severity || finding.Severity == severity;

    // The exact rule and the prefix combine by AND, which is what the original's query documents: it is
    // only useful when both are set consistently, and being strict about that is better than guessing.
    private static bool MatchesRule(FindingsQuery query, Claims.GateFinding finding) =>
        (query.RuleId is not { Length: > 0 } ruleId
            || string.Equals(finding.RuleId, ruleId, StringComparison.Ordinal))
        && (query.RulePrefix is not { Length: > 0 } prefix
            || finding.RuleId.StartsWith(prefix, StringComparison.Ordinal));
}
