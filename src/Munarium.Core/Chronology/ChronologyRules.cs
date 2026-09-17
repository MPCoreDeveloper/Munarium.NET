namespace Munarium.Chronology;

/// <summary>
/// The declarative chronology vocabulary: a deployment's rules for dates, their order and their
/// deadlines.
/// </summary>
/// <remarks>
/// The rules are data rather than code, because which dates must order each other is a property of the
/// workload and not of the kernel. A deployment that declares no rules never arms the gate at all,
/// which is why the chronology family is not part of the always-on five.
/// <para>
/// A target is either an absolute <c>subject.key</c> - which pairs across subjects, because it names
/// one exact claim - or a <c>*</c>-wildcard pattern over the claim key, which pairs within a subject.
/// That difference is what lets "the release date is after the draft date" and "every subject's
/// <c>*_deadline</c> follows its <c>*_date</c>" be the same kind of rule.
/// </para>
/// </remarks>
public sealed record ChronologyRules
{
    /// <summary>The key patterns that make a claim chronology-relevant when no override is declared.</summary>
    public static readonly IReadOnlyList<string> DefaultTemporalKeyPatterns =
        ["*_date", "*_on", "date_*", "*_deadline", "*_due", "*_when"];

    /// <summary>Gets the ordering rules: <c>before</c> must come first.</summary>
    public IReadOnlyList<OrderRule> Order { get; init; } = [];

    /// <summary>Gets the containment rules: <c>inner</c> must fall within <c>outer</c>.</summary>
    public IReadOnlyList<ContainsRule> Contains { get; init; } = [];

    /// <summary>Gets the rules forbidding two assertions from overlapping.</summary>
    public IReadOnlyList<OverlapRule> ForbidOverlap { get; init; } = [];

    /// <summary>Gets the deadline rules: an event is due within a number of days of another.</summary>
    public IReadOnlyList<DeadlineRule> Deadlines { get; init; } = [];

    /// <summary>Gets the duration rules: the elapsed time between two assertions must be in bounds.</summary>
    public IReadOnlyList<DurationRule> Durations { get; init; } = [];

    /// <summary>Gets the key patterns that make a claim temporal, replacing the defaults when declared.</summary>
    public IReadOnlyList<string> TemporalKeys { get; init; } = [];

    /// <summary>Gets a value indicating whether any rule was declared.</summary>
    public bool IsEmpty =>
        Order.Count == 0
        && Contains.Count == 0
        && ForbidOverlap.Count == 0
        && Deadlines.Count == 0
        && Durations.Count == 0;

    /// <summary>
    /// Lists every target any rule names.
    /// </summary>
    /// <returns>The targets, in rule order.</returns>
    public IReadOnlyList<string> AllTargets()
    {
        var targets = new List<string>();

        foreach (var rule in Order)
        {
            targets.Add(rule.Before);
            targets.Add(rule.After);
        }

        foreach (var rule in Contains)
        {
            targets.Add(rule.Outer);
            targets.Add(rule.Inner);
        }

        foreach (var rule in ForbidOverlap)
        {
            targets.Add(rule.A);
            targets.Add(rule.B);
        }

        foreach (var rule in Deadlines)
        {
            targets.Add(rule.Key);
            targets.Add(rule.DueFrom);
        }

        foreach (var rule in Durations)
        {
            targets.Add(rule.Start);
            targets.Add(rule.End);
        }

        return targets;
    }

    /// <summary>
    /// Lists the targets that name one exact claim, lowercased, for matching against a claim key.
    /// </summary>
    /// <returns>The absolute targets.</returns>
    public IReadOnlyList<string> AbsoluteTargets() =>
    [
        .. AllTargets()
            .Where(ChronologyPattern.IsAbsoluteTarget)
            .Select(target => target.ToLowerInvariant()),
    ];

    /// <summary>
    /// Reports whether a claim key is chronology-relevant.
    /// </summary>
    /// <remarks>
    /// A claim can also become relevant through an absolute target in a rule, which is why the gate
    /// consults both this and <see cref="AbsoluteTargets"/>.
    /// </remarks>
    /// <param name="key">The claim key's property part.</param>
    /// <returns><see langword="true"/> when the key matches a temporal pattern.</returns>
    public bool IsTemporalKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var patterns = TemporalKeys.Count == 0 ? DefaultTemporalKeyPatterns : TemporalKeys;
        return patterns.Any(pattern => ChronologyPattern.KeyPatternMatches(pattern, key));
    }
}

/// <summary>
/// The severity vocabulary a chronology rule declares.
/// </summary>
/// <remarks>
/// It is a string rather than an enum because it arrives in tenant-supplied rule data, and an
/// unrecognized value has to mean something harmless - a warning - rather than failing the request
/// that carried it.
/// </remarks>
public static class ChronologySeverity
{
    /// <summary>What a rule that declares nothing gets.</summary>
    public const string Default = "warn";

    /// <summary>The only value that disputes a claim.</summary>
    public const string Block = "block";
}
