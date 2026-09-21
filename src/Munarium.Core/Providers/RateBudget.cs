namespace Munarium.Providers;

/// <summary>
/// The window one configuration's declared budget is enforced over.
/// </summary>
/// <remarks>
/// Two windows, exactly as the original has them: a rolling minute for requests and tokens, and a UTC day per tier for
/// the token ceilings. A call is <em>checked</em> against its estimate before it is made and <em>recorded</em> with what
/// it cost afterwards, because an output length is not known until the model has answered - where the original consumes
/// the estimate atomically, which is the same guard for a rate and a worse one for a daily ceiling.
/// <para>
/// The state lives in the process that enforces it, which is faithful here: the original divides a configured ceiling
/// across replicas and draws its daily ledger from a shared store, and this deployment is one node by decision. A
/// restart therefore resets the windows, which is said in docs/providers-and-wire.md rather than left to be discovered.
/// </para>
/// </remarks>
/// <param name="budgets">What the configuration declared.</param>
/// <param name="now">Reads the current instant, so a test can move the window rather than wait for it.</param>
public sealed class RateBudget(ProviderBudgets budgets, Func<DateTimeOffset>? now = null)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly ProviderBudgets _budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _gate = new();
    private readonly long[] _dayTokens = new long[ModelTiers.All.Count];
    private DateTimeOffset _windowStarted;
    private DateOnly _day;
    private int _windowRequests;
    private long _windowTokens;

    /// <summary>
    /// Whether a call may be made, naming the ceiling that would be crossed.
    /// </summary>
    /// <param name="tier">The tier the call is billed to.</param>
    /// <param name="estimatedTokens">What the call is expected to cost at most, in tokens.</param>
    /// <returns>The reason to refuse, or <see langword="null"/> when the call may be made.</returns>
    public string? Check(ModelTier tier, long estimatedTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedTokens);

        if (_budgets.IsEmpty)
        {
            return null;
        }

        lock (_gate)
        {
            Roll();

            return Refusals(tier, estimatedTokens).FirstOrDefault(reason => reason is not null);
        }
    }

    /// <summary>One entry per ceiling the call would cross, in the order they are checked.</summary>
    /// <param name="tier">The tier the call is billed to.</param>
    /// <param name="estimatedTokens">What the call is expected to cost at most.</param>
    /// <returns>The reason each ceiling gives, or nothing when it holds.</returns>
    private IEnumerable<string?> Refusals(ModelTier tier, long estimatedTokens)
    {
        yield return _budgets.RequestsPerMinute is { } requests && _windowRequests + 1 > requests
            ? $"rpm budget {requests} is exhausted for this minute"
            : null;

        yield return _budgets.TokensPerMinute is { } tokens && _windowTokens + estimatedTokens > tokens
            ? $"tpm budget {tokens} is exhausted for this minute"
            : null;

        yield return _budgets.Daily(tier) is { } daily && _dayTokens[(int)tier] + estimatedTokens > daily
            ? $"the daily token ceiling {daily} is reached for the {ModelTiers.Name(tier)} tier"
            : null;
    }

    /// <summary>Records one request and what it cost, which is what the next check is measured against.</summary>
    /// <param name="tier">The tier the call was billed to.</param>
    /// <param name="tokens">What the call actually cost, in tokens.</param>
    public void Record(ModelTier tier, long tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);

        if (_budgets.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            Roll();

            _windowRequests++;
            _windowTokens += tokens;
            _dayTokens[(int)tier] += tokens;
        }
    }

    /// <summary>Rolls the minute and the day that have passed.</summary>
    private void Roll()
    {
        var moment = _now();

        if (moment - _windowStarted >= Window)
        {
            _windowStarted = moment;
            _windowRequests = 0;
            _windowTokens = 0;
        }

        var day = DateOnly.FromDateTime(moment.UtcDateTime);

        if (day != _day)
        {
            _day = day;
            Array.Clear(_dayTokens);
        }
    }
}
