namespace Munarium.Ledger;

using System.Diagnostics.CodeAnalysis;
using Posseth.UlidFactory;

/// <summary>
/// The ledger's identities, and the instant each one carries.
/// </summary>
/// <remarks>
/// A ledger identity is a ULID: sortable by the moment it was created, and - the part this port leans on
/// - <em>carrying</em> that moment in its first ten characters (a 48-bit count of milliseconds since the
/// Unix epoch). That is what lets the kernel answer "as of when", and "when was this written", without a
/// clock and without a timestamp column: the answer is inside the identity, so it cannot disagree with
/// it, and a snapshot that holds those identities holds its own time.
/// <para>
/// A clock read in a kernel makes every verdict that depends on it unreproducible: the reader cannot
/// rebuild the answer later, because the clock has moved. Ids that carry their instant move that
/// question into the data, where a pin already lives.
/// </para>
/// </remarks>
public static class LedgerIds
{
    /// <summary>
    /// Creates an identity for the current instant.
    /// </summary>
    /// <returns>The identity, as a 26-character ULID.</returns>
    public static string New() => Ulid.NewUlid().Value;

    /// <summary>
    /// Creates an identity for an explicit instant.
    /// </summary>
    /// <remarks>
    /// The explicit form is what a caller uses when it already knows the time something happened - a
    /// backfilled import, a reconciled observation - so the identity records when it happened rather than
    /// when the import ran. Precision is a millisecond, which is what the format holds.
    /// </remarks>
    /// <param name="instant">The instant the identity belongs to.</param>
    /// <returns>The identity, as a 26-character ULID.</returns>
    public static string NewAt(DateTimeOffset instant) => Ulid.NewUlid(instant).Value;

    /// <summary>
    /// Reports whether a value is an identity this ledger could have handed out.
    /// </summary>
    /// <remarks>
    /// Opaque identities are still accepted everywhere - a caller may name its own claims, and the
    /// original does exactly that - so this is a question a caller asks, never a check the kernel
    /// imposes.
    /// </remarks>
    /// <param name="value">The value to inspect.</param>
    /// <returns><see langword="true"/> when the value parses as a ULID.</returns>
    public static bool IsLedgerId([NotNullWhen(true)] string? value) => Ulid.TryParse(value, out _);

    /// <summary>
    /// Reads the instant an identity was created at.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> for anything that is not a ULID, and for a ULID whose timestamp
    /// cannot be represented as a date. The second case is not hypothetical - the format's 48 bits reach
    /// the year 10889 - and it is the same stance the chronology rules take on an unrepresentable bound:
    /// a value that cannot be expressed cannot be used, so nothing is invented in its place.
    /// </remarks>
    /// <param name="value">The identity.</param>
    /// <returns>The instant, or <see langword="null"/>.</returns>
    public static DateTimeOffset? InstantOf(string? value)
    {
        if (!Ulid.TryParse(value, out var ulid) || ulid is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ulid.ToUnixTime());
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the newest instant any of a set of identities carries.
    /// </summary>
    /// <param name="values">The identities; values that are not ULIDs are ignored.</param>
    /// <returns>The newest instant, or <see langword="null"/> when none carries one.</returns>
    public static DateTimeOffset? NewestInstantOf(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        DateTimeOffset? newest = null;

        foreach (var value in values)
        {
            if (InstantOf(value) is { } instant && (newest is null || instant > newest))
            {
                newest = instant;
            }
        }

        return newest;
    }

    /// <summary>
    /// The order the identities give you: by the instant each carries, then by the identity itself.
    /// </summary>
    /// <remarks>
    /// A ULID is ordered by construction - Crockford Base32 preserves order, so comparing two of them
    /// ordinally <em>is</em> comparing the moments they were created at, with the random tail deciding
    /// inside one millisecond. That makes an identity carry three things at once: what it is, when it
    /// happened, and how it sorts against its neighbours, without a fourth column for any of them.
    /// <para>
    /// This is a <em>time</em> order, not the ledger's order, and the difference is load-bearing. The
    /// ledger's order is the position the store assigned, and the pin - the thing that makes a read
    /// reproducible - is expressed in that position. A clock is not a ledger: two writers whose clocks
    /// disagree, or two writes inside one millisecond, produce an identity order that is not the order
    /// the writes landed in. Resolution therefore keeps reading sequences, and this comparer is what a
    /// caller uses when it has identities but no positions - a pre-acceptance batch, an ingest run, a
    /// list of ids out of a document.
    /// </para>
    /// <para>
    /// Total and deterministic over any mix: identities that are not ULIDs carry no instant, so a
    /// comparison they take part in falls back to ordinal text order. That fallback is a tie-break, not a
    /// claim about time, which is why a mixed set is not a timeline.
    /// </para>
    /// </remarks>
    public static IComparer<string> ChronologicalComparer { get; } = new ByInstantThenText();

    /// <summary>
    /// Orders identities by the instant they carry.
    /// </summary>
    /// <param name="values">The identities.</param>
    /// <returns>The identities, oldest first.</returns>
    public static IReadOnlyList<string> InOrder(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return
        [
            .. values
                .OfType<string>()
                .Where(value => value.Length > 0)
                .Order(ChronologicalComparer),
        ];
    }

    private sealed class ByInstantThenText : IComparer<string>
    {
        /// <inheritdoc />
        public int Compare(string? left, string? right)
        {
            if (InstantOf(left) is { } leftInstant && InstantOf(right) is { } rightInstant)
            {
                var byInstant = leftInstant.CompareTo(rightInstant);
                if (byInstant != 0)
                {
                    return byInstant;
                }
            }

            return string.CompareOrdinal(left, right);
        }
    }
}
