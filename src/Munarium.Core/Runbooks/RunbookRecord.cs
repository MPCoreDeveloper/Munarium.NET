namespace Munarium.Runbooks;

using System.Globalization;

/// <summary>
/// One applied runbook version, as the deployment records it.
/// </summary>
/// <remarks>
/// The identity is the reference rather than the name, so every version an operator applied is its own row and an
/// earlier version stays readable after a newer one lands: a session pins <c>name@version</c>, and a pin that could be
/// outlived by its own document would not be a pin.
/// <para>
/// The YAML is kept verbatim beside the mapping it produced. The mapping is what runs; the bytes are what was applied,
/// and the two are the difference between "this is what the document says" and "this is what an operator wrote".
/// </para>
/// </remarks>
public sealed record RunbookRecord
{
    /// <summary>Gets the tenant the runbook belongs to, which is half of its key.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the pinned reference, <c>name@version</c>.</summary>
    public required string Ref { get; init; }

    /// <summary>Gets the YAML as it was applied.</summary>
    public required string Yaml { get; init; }

    /// <summary>Gets where the runbook stands.</summary>
    public RunbookStatus Status { get; init; } = RunbookStatus.Active;

    /// <summary>Gets the removal this row is armed with, when one is in flight.</summary>
    public string? RemovalId { get; init; }

    /// <summary>Gets when the removal was asked for, stamped by the caller that asked.</summary>
    public string? RemovalRequestedAt { get; init; }

    /// <summary>Gets who asked for the removal.</summary>
    public string? RemovalRequestedBy { get; init; }

    /// <summary>Gets when the removal was confirmed, stamped by the caller that confirmed it.</summary>
    public string? RemovedAt { get; init; }

    /// <summary>Gets when the version was first applied, stamped by the store.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>Gets when it was last applied, stamped by the store.</summary>
    public string? UpdatedAt { get; init; }

    /// <summary>Gets the name, which is the reference without its version.</summary>
    public string Name => Split(Ref).Name;

    /// <summary>Gets the version, or <see langword="null"/> when the reference names none.</summary>
    public int? Version => Split(Ref).Version;

    /// <summary>
    /// Splits a reference into its name and version.
    /// </summary>
    /// <remarks>
    /// A split on the first <c>@</c> is unambiguous because a runbook name may not contain one: the reader refuses that
    /// outright, since a name carrying an <c>@</c> would poison both this reference and the numeric ordering of a
    /// runbook's versions.
    /// </remarks>
    /// <param name="runbookRef">The reference.</param>
    /// <returns>The two halves, with no version when the reference names none.</returns>
    public static (string Name, int? Version) Split(string runbookRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runbookRef);

        var at = runbookRef.IndexOf('@', StringComparison.Ordinal);

        if (at < 0)
        {
            return (runbookRef, null);
        }

        var version = runbookRef[(at + 1)..];

        return (
            runbookRef[..at],
            int.TryParse(version, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null);
    }
}
