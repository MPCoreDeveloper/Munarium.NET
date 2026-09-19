namespace Munarium.Runbooks;

using Munarium.Sessions;

/// <summary>A version that was applied, with the mapping it produced.</summary>
public sealed record RunbookApplied
{
    /// <summary>Gets the record as stored.</summary>
    public required RunbookRecord Record { get; init; }

    /// <summary>Gets the document the YAML mapped onto, which is what a turn will read.</summary>
    public required RunbookDocument Document { get; init; }
}

/// <summary>A version resolved into the document a turn reads.</summary>
public sealed record RunbookResolved
{
    /// <summary>Gets the record the document came from.</summary>
    public required RunbookRecord Record { get; init; }

    /// <summary>Gets the document.</summary>
    public required RunbookDocument Document { get; init; }
}

/// <summary>An application that was refused, and why.</summary>
/// <param name="Code">The kebab-case code, from <see cref="RunbookRefusalCodes"/>.</param>
/// <param name="Message">The message, which has to be safe to show an operator.</param>
public sealed record RunbookRefusal(string Code, string Message);

/// <summary>
/// The codes an application can refuse with.
/// </summary>
public static class RunbookRefusalCodes
{
    /// <summary>The document could not be read as a runbook.</summary>
    public const string Invalid = "runbook-invalid";

    /// <summary>The version was removed, so the same reference may not be applied again.</summary>
    public const string Removed = "runbook-removed";
}

/// <summary>
/// The result of applying a runbook.
/// </summary>
public readonly union RunbookApplication(RunbookApplied, RunbookRefusal);

/// <summary>
/// Applying runbooks, and resolving them back into the document a turn reads.
/// </summary>
/// <remarks>
/// The parse and the store are separate concerns and this is where they meet. Reading is the reader's, keeping is the
/// store's, and what lives here is the order plus the two rules that only make sense together: a document that cannot be
/// read is never stored, and a version that was removed is never resurrected by re-applying its own reference - an
/// operator who wants it back publishes a new version, which is a different reference and leaves the earlier turn records
/// pointing at the document they actually ran on.
/// <para>
/// Applying does not require the document to be <em>wise</em>, only readable: the findings
/// (<see cref="RunbookValidation"/>) are what an operator reads before applying, and refusing on a warning about a
/// cutover that will publish without a human would be refusing a runbook that runs.
/// </para>
/// </remarks>
public static class RunbookCatalog
{
    /// <summary>
    /// Applies a runbook from the YAML an operator wrote.
    /// </summary>
    /// <param name="store">Where the version is kept.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="yaml">The document as written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The applied version, or the refusal.</returns>
    public static async ValueTask<RunbookApplication> ApplyAsync(
        IRunbookStore store,
        string tenant,
        string yaml,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(yaml);

        var (document, problem) = RunbookReader.Read(yaml);

        if (document is null)
        {
            return new RunbookRefusal(
                RunbookRefusalCodes.Invalid,
                problem ?? "the document could not be read as a runbook");
        }

        var runbookRef = SessionCreation.Pin(document);

        // A removed version is not applyable again, and the message says what to do instead rather than why not.
        if (await store
                .GetAsync(tenant, runbookRef, includeRemoved: true, cancellationToken)
                .ConfigureAwait(false) is { Status: RunbookStatus.Removed })
        {
            return new RunbookRefusal(
                RunbookRefusalCodes.Removed,
                $"runbook '{runbookRef}' was removed; publish a new version instead");
        }

        var record = await store
            .ApplyAsync(new RunbookRecord { Tenant = tenant, Ref = runbookRef, Yaml = yaml }, cancellationToken)
            .ConfigureAwait(false);

        return new RunbookApplied { Record = record, Document = document };
    }

    /// <summary>
    /// Resolves whatever a caller named into the document a turn reads.
    /// </summary>
    /// <remarks>
    /// The YAML is re-read rather than a mapping being stored beside it: the bytes are what was applied, so a document
    /// that no longer maps is reported as unresolvable rather than served from a cached mapping a reader has since
    /// learned to refuse.
    /// </remarks>
    /// <param name="store">Where the versions are kept.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="nameOrRef">The name, or the reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved version, or <see langword="null"/> when there is none to read.</returns>
    public static async ValueTask<RunbookResolved?> ResolveAsync(
        IRunbookStore store,
        string tenant,
        string nameOrRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrRef);

        var record = await store
            .ResolveAsync(tenant, nameOrRef, includeRemoved: false, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        var (document, _) = RunbookReader.Read(record.Yaml);

        return document is null ? null : new RunbookResolved { Record = record, Document = document };
    }
}
