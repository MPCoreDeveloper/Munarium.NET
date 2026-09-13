namespace Munarium.Context;

using Munarium.Ledger;

/// <summary>One titled block of a composed context.</summary>
/// <param name="Title">The section's title.</param>
/// <param name="Body">The section's text.</param>
public sealed record ContextSection(string Title, string Body);

/// <summary>
/// A request to compose the context a model would be given.
/// </summary>
/// <remarks>
/// Where the original design scopes a context by a hierarchical scope path, this kernel scopes by version
/// and by shape: those are the two identities a fact actually carries, and inventing a third would be
/// describing something the ledger does not have.
/// </remarks>
public sealed record ContextRequest
{
    /// <summary>Gets the pin to compose as of. 0 means the present.</summary>
    public SequenceNumber Pin { get; init; } = SequenceNumber.Zero;

    /// <summary>Gets the version to compose from, or an empty string for every version.</summary>
    public string VersionId { get; init; } = string.Empty;

    /// <summary>Gets the shape to compose from, or an empty string for every shape.</summary>
    public string Shape { get; init; } = string.Empty;

    /// <summary>Gets the token budget for facts. 0 means unbounded.</summary>
    public int BudgetTokens { get; init; }

    /// <summary>Gets how many facts may be included. 0 means unbounded.</summary>
    public int FactLimit { get; init; }
}

/// <summary>
/// The context that was composed: its sections, its text, what it costs to send, and what it covers.
/// </summary>
/// <param name="Sections">The sections, in the order they were composed.</param>
/// <param name="Text">The composed text, which is the concatenation of the section bodies.</param>
/// <param name="EstimatedTokens">The token estimate for <paramref name="Text"/>.</param>
/// <param name="ContentHash">SHA-256 over <paramref name="Text"/>, lowercase hex.</param>
/// <param name="Pin">The pin the context was composed at.</param>
public sealed record ComposedContext(
    IReadOnlyList<ContextSection> Sections,
    string Text,
    int EstimatedTokens,
    string ContentHash,
    SequenceNumber Pin);
