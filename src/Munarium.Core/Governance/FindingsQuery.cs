namespace Munarium.Governance;

using Munarium.Claims;
using Munarium.Ledger;

/// <summary>
/// The question a findings read answers: whose findings, as of when, and how many.
/// </summary>
/// <remarks>
/// The properties mirror the original's query, which is what the REST surface exposes: a version, a pin,
/// an exact rule, a rule prefix and a limit.
/// </remarks>
public sealed record FindingsQuery
{
    /// <summary>Gets the position to read as of, or <see langword="null"/> for every finding recorded.</summary>
    public SequenceNumber? AsOfSequence { get; init; }

    /// <summary>Gets the severity to select, or <see langword="null"/> for every severity.</summary>
    public Severity? Severity { get; init; }

    /// <summary>Gets the exact rule id to select, or <see langword="null"/> for every rule.</summary>
    public string? RuleId { get; init; }

    /// <summary>
    /// Gets a rule-id prefix to select, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <c>gate.</c> selects every kernel gate's finding, <c>matrix.</c> every connector's. It combines with
    /// <see cref="RuleId"/> by AND, which is only useful when both are set consistently.
    /// </remarks>
    public string? RulePrefix { get; init; }

    /// <summary>Gets how many findings to return, oldest first, or <see langword="null"/> for all.</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// One recorded finding, stamped with the position its write settled at.
/// </summary>
/// <remarks>
/// The stamp is what makes a finding citable: a reader can point at the write that produced it, and a pin
/// taken at that position still sees it. Every finding of one write carries the same stamp, because the
/// verdict belongs to the write and not to the individual rule that produced it.
/// </remarks>
/// <param name="Sequence">The position the write settled at.</param>
/// <param name="Finding">The finding.</param>
public sealed record StoredFinding(SequenceNumber Sequence, GateFinding Finding);
