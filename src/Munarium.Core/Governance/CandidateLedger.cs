namespace Munarium.Governance;

using Munarium.Claims;
using Munarium.Chronology;
using Munarium.Facts;
using Munarium.Governance.Gates;
using Munarium.Ledger;

/// <summary>
/// The ledger's write path for a candidate: judge the whole unit against the head it was judged against,
/// then land it as one atomic append.
/// </summary>
/// <remarks>
/// The per-claim command path and this one differ in what a unit is. A command is one claim whose verdict
/// is its own; a candidate is everything a writer produced for a scope at once - the proposals, and the
/// text the text-shaped gates read - so the gates that cannot see a claim at all (leakage, repetition) get
/// something to judge, and a batch lands entirely or not at all.
/// <para>
/// Two properties are load-bearing. A blocked claim is recorded as <em>disputed</em> and never dropped, so
/// the ledger carries the claim and the refusal together. And the gate decision is only valid for the head
/// it was computed against: the append is conditional on the head the snapshot was read at, because a
/// concurrent writer advancing the head between the two would land claims gated against canon they no
/// longer describe.
/// </para>
/// </remarks>
public sealed class CandidateLedger
{
    private readonly IStorageBackend _storage;
    private readonly MeshSnapshotBuilder _snapshots;
    private readonly ChronologyRules? _chronology;
    private readonly bool _armAbsenceCheck;
    private readonly int _maxReGateAttempts;

    /// <summary>
    /// Initializes a new instance of the <see cref="CandidateLedger"/> class.
    /// </summary>
    /// <param name="storage">The ledger's storage seam.</param>
    /// <param name="snapshots">Where the pinned view the gates read is assembled.</param>
    /// <param name="chronology">The armed chronology rules, or <see langword="null"/> to leave the family off.</param>
    /// <param name="armAbsenceCheck">
    /// Whether the deadline-absence check runs. It is off by default, which is what the original's server
    /// does - it has no as-of clock. This port can arm it because a snapshot's identities carry the instant
    /// it was written at, so the answer stays reproducible from the snapshot alone.
    /// </param>
    /// <param name="maxReGateAttempts">How many times a contended batch is re-gated and re-appended.</param>
    public CandidateLedger(
        IStorageBackend storage,
        MeshSnapshotBuilder snapshots,
        ChronologyRules? chronology = null,
        bool armAbsenceCheck = false,
        int maxReGateAttempts = 3)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _chronology = chronology;
        _armAbsenceCheck = armAbsenceCheck;
        _maxReGateAttempts = maxReGateAttempts > 0
            ? maxReGateAttempts
            : throw new ArgumentOutOfRangeException(
                nameof(maxReGateAttempts),
                maxReGateAttempts,
                "Must be positive.");
    }

    /// <summary>
    /// Judges a candidate and records it.
    /// </summary>
    /// <param name="versionId">The version whose canon the candidate is judged against.</param>
    /// <param name="claims">The proposals in the batch.</param>
    /// <param name="candidateText">The text the text-shaped gates read, when the candidate produced one.</param>
    /// <param name="expectedHead">
    /// The head the caller requires, or <see langword="null"/> to append at whatever the head is. A caller
    /// that pins one gets a contention back instead of a re-gate, because it asked for a position rather
    /// than for an append.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recorded batch, or the contention that stopped it.</returns>
    /// <exception cref="ArgumentException">Thrown when the batch carries neither a claim nor text.</exception>
    public async ValueTask<CandidateOutcome> AppendAsync(
        string versionId,
        IReadOnlyList<ProposedClaim> claims,
        string? candidateText = null,
        SequenceNumber? expectedHead = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentNullException.ThrowIfNull(claims);

        if (claims.Count == 0 && string.IsNullOrEmpty(candidateText))
        {
            throw new ArgumentException("A candidate with no claim and no text is not a write.", nameof(claims));
        }

        var stream = StreamId.From(versionId);
        var lastExpected = expectedHead ?? SequenceNumber.Zero;
        var lastActual = lastExpected;

        for (var attempt = 1; attempt <= _maxReGateAttempts; attempt++)
        {
            var head = await _storage.HeadAsync(stream, cancellationToken).ConfigureAwait(false);

            if (expectedHead is { } callerPin && callerPin != head)
            {
                return new CandidateContended(callerPin, head);
            }

            var snapshot = await _snapshots
                .BuildAsync(versionId, pin: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var candidate = CandidateOf(claims, candidateText);
            var findings = Judge(snapshot, candidate);
            var blocked = DeterministicGates.BlockedClaimKeys(findings);

            var stored = claims
                .Select(claim => Fact(versionId, claim, findings, blocked))
                .ToList();

            var entries = new List<LedgerEvent>(stored.Count + 1);

            entries.AddRange(stored.Select(fact => new LedgerEvent(
                fact.IsDisputed ? FactCodec.DisputedEventType : FactCodec.AssertedEventType,
                FactCodec.Encode(fact))));

            // The findings travel in the same conditional append as the claims they judge, which is
            // deliberately stronger than the original: it records them in a separate table and treats a
            // failure as a warning, because failing the request would push a client into a retry that
            // appends again. One append cannot disagree with its own response, so there is nothing to warn
            // about - and a write that produced no findings records no event.
            if (findings.Count > 0)
            {
                entries.Add(new LedgerEvent(FindingCodec.FindingsEventType, FindingCodec.Encode(findings)));
            }

            if (entries.Count == 0)
            {
                return new CandidateRecorded([], findings, head);
            }

            // One conditional append, pinned at the head the gates read: the batch is atomic in the store,
            // so a partial landing is not a state this can be in.
            var result = await _storage
                .AppendAsync(stream, head, entries, cancellationToken)
                .ConfigureAwait(false);

            if (result is Appended appended)
            {
                return new CandidateRecorded(
                    Positioned(stored, appended.Head, entries.Count),
                    findings,
                    appended.Head,
                    findings.Count > 0 ? appended.Head : null);
            }

            if (result is VersionConflict conflict)
            {
                lastExpected = conflict.Expected;
                lastActual = conflict.Actual;

                // The head moved under us. Without a caller pin that is not an error - the caller asked for
                // an append, not for a position - so the batch is re-read, re-gated and re-appended.
                if (expectedHead is null && attempt < _maxReGateAttempts)
                {
                    continue;
                }

                return new CandidateContended(lastExpected, lastActual);
            }
        }

        return new CandidateContended(lastExpected, lastActual);
    }

    private List<GateFinding> Judge(MeshSnapshot snapshot, Candidate candidate)
    {
        var findings = new List<GateFinding>(DeterministicGates.Run(snapshot, candidate));

        if (_chronology is { } rules)
        {
            findings.AddRange(ChronologyGate.Evaluate(
                snapshot,
                candidate,
                rules,
                _armAbsenceCheck ? snapshot.WrittenOn : null));
        }

        return findings;
    }

    private static Candidate CandidateOf(IReadOnlyList<ProposedClaim> claims, string? text) => new()
    {
        // A batch may span scopes; the candidate's own scope - the one a finding is reported against - is
        // the first one any proposal names.
        ScopePath = claims.Select(claim => claim.ScopePath).FirstOrDefault(scope => scope is not null),
        Text = text ?? string.Empty,
        Claims = [.. claims.Where(claim => claim.ClaimType is not ClaimType.Correction)],
        Corrections = [.. claims.Where(claim => claim.ClaimType is ClaimType.Correction)],
    };

    private static FactRecord Fact(
        string versionId,
        ProposedClaim claim,
        IReadOnlyList<GateFinding> findings,
        IReadOnlySet<string> blocked)
    {
        // The rule that refused a claim is the reason the ledger keeps with it; the finding keeps the
        // detail. A claim the gates did not block carries neither.
        var refusal = blocked.Contains(claim.ClaimKey)
            ? findings.FirstOrDefault(finding =>
                finding.Severity is Severity.Block &&
                string.Equals(finding.ClaimKey, claim.ClaimKey, StringComparison.Ordinal))
            : null;

        return new FactRecord
        {
            // The ledger hands out the identity, and it is a ULID: the claim therefore carries the instant
            // it was written at without a field for it.
            ClaimId = LedgerIds.New(),
            VersionId = versionId,
            ClaimType = claim.ClaimType,
            // A proposal names its own identity, so the claim key IS the supersession lineage - which is
            // what makes a later claim on the same key supersede this one.
            Lineage = claim.ClaimKey,
            Subject = claim.Subject,
            Key = claim.Key,
            Value = claim.Value,
            ScopePath = claim.ScopePath,
            Provenance = claim.Provenance,
            SupersedesId = claim.SupersedesId,
            Body = string.Empty,
            Statement = string.Empty,
            Actor = string.Empty,
            Gate = refusal?.RuleId ?? string.Empty,
            Reason = refusal?.Message ?? string.Empty,
        };
    }

    private static IReadOnlyList<Claim> Positioned(List<FactRecord> facts, SequenceNumber head, int appended)
    {
        // The append that moved the head to H wrote all of them, so the first one sits at H-N+1. The count
        // is the whole batch, not the claims in it: the findings event occupies the position after them.
        var first = head.Value - appended + 1;

        return [.. facts.Select((fact, index) => ClaimProjection.Of(fact, new SequenceNumber(first + index)))];
    }
}
