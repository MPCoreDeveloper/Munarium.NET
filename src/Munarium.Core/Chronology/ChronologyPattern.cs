namespace Munarium.Chronology;

/// <summary>
/// Target matching: which timeline events a rule's target selects.
/// </summary>
/// <remarks>
/// A target is one of two things, and which one it is decides how it pairs. A target containing a dot
/// and no wildcard is one exact <c>subject.key</c>, so it can pair two claims about different subjects.
/// Anything else is a pattern over the property alone, so it pairs within a subject: a rule saying that
/// every subject's <c>*_deadline</c> follows its own <c>*_date</c> must not compare one subject's
/// deadline with another's date.
/// </remarks>
public static class ChronologyPattern
{
    /// <summary>
    /// Reports whether a target names one exact claim.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <returns><see langword="true"/> when the target is an absolute <c>subject.key</c>.</returns>
    public static bool IsAbsoluteTarget(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.Contains('.', StringComparison.Ordinal)
            && !target.Contains('*', StringComparison.Ordinal);
    }

    /// <summary>
    /// Matches a <c>*</c>-wildcard pattern against a claim key, case-insensitively, in full.
    /// </summary>
    /// <remarks>
    /// This is a full match rather than a search: the literals between the wildcards are consumed in
    /// order, the first one is anchored at the start and the last one at the end. A pattern with no
    /// wildcard at all is therefore equality, which is what makes <c>*_due</c> and <c>due</c> mean
    /// different things.
    /// </remarks>
    /// <param name="pattern">The pattern.</param>
    /// <param name="key">The claim key's property part.</param>
    /// <returns><see langword="true"/> when the pattern matches the key in full.</returns>
    public static bool KeyPatternMatches(string pattern, string key)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(key);

        var subject = key.ToLowerInvariant();
        var lowered = pattern.ToLowerInvariant();
        var parts = lowered.Split('*');
        var position = 0;

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0)
            {
                continue;
            }

            if (index == 0)
            {
                if (!subject.StartsWith(part, StringComparison.Ordinal))
                {
                    return false;
                }

                position = part.Length;
                continue;
            }

            var found = subject.IndexOf(part, position, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            position = found + part.Length;
        }

        var last = parts[^1];
        if (last.Length == 0)
        {
            return true;
        }

        // A pattern with no wildcard at all is equality, not a prefix match: '_due' would otherwise
        // match 'not_due' as well.
        return subject.EndsWith(last, StringComparison.Ordinal)
            && (parts.Length > 1 || string.Equals(subject, lowered, StringComparison.Ordinal));
    }
}
