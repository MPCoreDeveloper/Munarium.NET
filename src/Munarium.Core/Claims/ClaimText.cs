namespace Munarium.Claims;

/// <summary>
/// The canonical text form of a claim - <c>subject.key=value</c> - and the comparisons built on it.
/// </summary>
/// <remarks>
/// This is the one shape every gate parses, so it is implemented in one place: a gate that split the
/// text its own way would be a second definition of what a claim says, and the two would drift.
/// <para>
/// Comparison folds case and collapses whitespace, because "Approved" and "approved  " are the same
/// value to a reader and a governance gate that claimed otherwise would block on formatting. The fold
/// is invariant-culture rather than locale-aware: a gate's verdict must not change with the machine's
/// regional settings.
/// </para>
/// </remarks>
public static class ClaimText
{
    /// <summary>
    /// Canonicalizes a claim into <c>subject.key=value</c>.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="key">The property.</param>
    /// <param name="value">The value.</param>
    /// <returns>The canonical text, with each part trimmed.</returns>
    public static string Normalize(string subject, string key, string value) =>
        string.Concat(subject.Trim(), ".", key.Trim(), "=", value.Trim());

    /// <summary>
    /// Splits canonical text back into its parts, tolerating free text.
    /// </summary>
    /// <remarks>
    /// A key is split at the <em>last</em> dot, not the first: keys are dotted identifiers
    /// (<c>release.date</c>), so the first-dot reading would turn the key into part of the subject.
    /// Text with no <c>=</c> is returned as a value with no subject or key, and text whose head has no
    /// dot yields an empty subject - both are inputs the gates do see, and neither is worth an
    /// exception when the parse can be total.
    /// </remarks>
    /// <param name="text">The text to split.</param>
    /// <returns>The subject, the key and the value.</returns>
    public static (string Subject, string Key, string Value) Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var separator = text.IndexOf('=', StringComparison.Ordinal);
        if (separator < 0)
        {
            return (string.Empty, string.Empty, text.Trim());
        }

        var head = text[..separator].Trim();
        var value = text[(separator + 1)..].Trim();
        var lastDot = head.LastIndexOf('.');

        return lastDot < 0
            ? (string.Empty, head, value)
            : (head[..lastDot].Trim(), head[(lastDot + 1)..].Trim(), value);
    }

    /// <summary>
    /// Compares two values the way the gates do: case- and whitespace-insensitively.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> when the values are equivalent.</returns>
    public static bool ValuesEquivalent(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return string.Equals(Fold(left), Fold(right), StringComparison.Ordinal);
    }

    private static string Fold(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
