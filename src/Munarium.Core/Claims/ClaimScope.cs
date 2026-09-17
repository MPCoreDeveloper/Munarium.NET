namespace Munarium.Claims;

/// <summary>
/// The rule a scope prefix selects by.
/// </summary>
/// <remarks>
/// One definition, because two would drift: the reference resolution and the snapshot builder both answer
/// "is this claim in the scope I asked for", and a reader that got a different answer from the two would
/// have no way to tell which was wrong.
/// <para>
/// A prefix matches the scope itself and its descendants, and stops at a segment boundary - <c>book.ch1</c>
/// selects <c>book.ch1</c> and <c>book.ch1.scene2</c>, and not <c>book.ch10</c>. A claim with no scope is
/// not in any named scope, so a prefix excludes it.
/// </para>
/// </remarks>
public static class ClaimScope
{
    /// <summary>
    /// Reports whether a scope is selected by a prefix.
    /// </summary>
    /// <param name="scope">The claim's scope, or <see langword="null"/> when it has none.</param>
    /// <param name="prefix">The prefix, or <see langword="null"/> to select every scope.</param>
    /// <returns><see langword="true"/> when the claim is in scope.</returns>
    public static bool Matches(string? scope, string? prefix) => prefix switch
    {
        null => true,
        _ when scope is null => false,
        _ => string.Equals(scope, prefix, StringComparison.Ordinal)
            || scope.StartsWith(string.Concat(prefix, "."), StringComparison.Ordinal),
    };
}
