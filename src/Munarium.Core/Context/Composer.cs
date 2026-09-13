namespace Munarium.Context;

using System.Security.Cryptography;
using System.Text;
using Munarium.Facts;

/// <summary>
/// Composes the context a model would be given: the facts that hold in a scope, within a token budget.
/// </summary>
/// <remarks>
/// Composition is a pure function of the pinned facts, so the same pin composes the same text and therefore
/// the same content hash - which is what makes the hash usable as a cache key rather than a guess. Nothing
/// is stored: a context is derived from the ledger on demand, like a slice.
/// </remarks>
/// <param name="facts">The read model the facts are read from.</param>
public sealed class Composer(FactLedger facts)
{
    /// <summary>The token estimate this composer uses: four characters per token, rounded up.</summary>
    /// <remarks>
    /// Deliberately not a tokeniser. A real one is model-specific, and the estimate has to be reproducible
    /// on every machine or the content hash means nothing; four characters per token is the documented
    /// approximation, and a caller that needs better can measure on top of it.
    /// </remarks>
    public const int CharactersPerToken = 4;

    private const string FactsTitle = "Facts";
    private const string DisputedTitle = "Disputed";

    private readonly FactLedger _facts = facts ?? throw new ArgumentNullException(nameof(facts));

    /// <summary>Composes a context.</summary>
    /// <param name="request">What to compose.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The composed context.</returns>
    public async ValueTask<ComposedContext> ComposeAsync(
        ContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pin = request.Pin.Value > 0
            ? request.Pin
            : await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false);

        var slice = await _facts.SliceAsync(pin, cancellationToken).ConfigureAwait(false);

        var scoped = slice
            .Facts
            .Where(sliced => InScope(sliced.Fact, request))
            .Select(static sliced => sliced.Fact)
            .ToList();

        var accepted = scoped.Where(static fact => !fact.IsDisputed).ToList();
        var refused = scoped.Where(static fact => fact.IsDisputed).ToList();

        var sections = new List<ContextSection>();

        ComposeAccepted(accepted, request, sections);

        // Refusals are not budgeted: a context that hid what the ledger disputed would be the one thing this
        // system exists to prevent, and they are few by construction.
        if (refused.Count > 0)
        {
            var body = new StringBuilder();

            foreach (var fact in refused)
            {
                body.Append(Line(fact));
            }

            sections.Add(new ContextSection(DisputedTitle, body.ToString()));
        }

        var text = string.Concat(sections.Select(static section => section.Body));

        return new ComposedContext(sections, text, EstimateTokens(text.Length), Hash(text), pin);
    }

    /// <summary>Estimates the tokens in a piece of text.</summary>
    /// <param name="length">The text's length, in characters.</param>
    /// <returns>The estimate.</returns>
    public static int EstimateTokens(int length) =>
        (length + CharactersPerToken - 1) / CharactersPerToken;

    private static void ComposeAccepted(
        List<FactRecord> accepted,
        ContextRequest request,
        List<ContextSection> sections)
    {
        if (accepted.Count == 0)
        {
            return;
        }

        var body = new StringBuilder();
        var included = 0;

        foreach (var fact in accepted)
        {
            var line = Line(fact);

            if ((request.FactLimit > 0 && included >= request.FactLimit) ||
                (request.BudgetTokens > 0 && EstimateTokens(body.Length + line.Length) > request.BudgetTokens))
            {
                break;
            }

            body.Append(line);
            included++;
        }

        var omitted = accepted.Count - included;

        if (omitted > 0)
        {
            body.Append("... ").Append(omitted).Append(" more accepted fact(s) omitted\n");
        }

        sections.Add(new ContextSection(FactsTitle, body.ToString()));
    }

    // One line per fact: what was claimed, the structured body it was claimed with, and the lineage that
    // identifies it. A disputed fact also says why, because the refusal is part of what the ledger knows.
    private static string Line(FactRecord fact) =>
        fact.IsDisputed
            ? $"- {fact.Statement} | {fact.Body} | {fact.Lineage} [{fact.Gate}: {fact.Reason}]\n"
            : $"- {fact.Statement} | {fact.Body} | {fact.Lineage}\n";

    private static bool InScope(FactRecord fact, ContextRequest request) =>
        (request.VersionId.Length == 0 || string.Equals(fact.VersionId, request.VersionId, StringComparison.Ordinal)) &&
        (request.Shape.Length == 0 || fact.Lineage.StartsWith(request.Shape + "@", StringComparison.Ordinal));

    private static string Hash(string text)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(text));

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
