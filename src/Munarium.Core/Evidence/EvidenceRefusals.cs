namespace Munarium.Evidence;

/// <summary>
/// Builds the refusals a provider reports instead of failing.
/// </summary>
/// <remarks>
/// One place, because every provider owes the caller the same thing: a block that says what the layer could not show
/// and why, rather than an exception that collapses "the register declined to say" into "something broke". Those are
/// different answers to give a user, and the first one is often the answer.
/// </remarks>
public static class EvidenceRefusals
{
    /// <summary>
    /// Builds a refusal.
    /// </summary>
    /// <param name="code">The kebab-case code, from <see cref="EvidenceRefusalCodes"/>.</param>
    /// <param name="message">The message, which has to be safe to show a caller.</param>
    /// <param name="source">The source it is about, when naming it is safe.</param>
    /// <returns>The refusal block.</returns>
    public static EvidenceBlock Refuse(string code, string message, string? source = null) => new EvidenceRefusal
    {
        Code = code,
        Message = message,
        Source = source,
    };
}
