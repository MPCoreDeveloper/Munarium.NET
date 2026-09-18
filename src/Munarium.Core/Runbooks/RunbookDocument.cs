namespace Munarium.Runbooks;

/// <summary>
/// A runbook's identity: the name and version an operator refers to it by.
/// </summary>
public sealed record RunbookMeta
{
    /// <summary>Gets the runbook's name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the runbook's version.</summary>
    public required int Version { get; init; }
}

/// <summary>
/// A parsed runbook: what evidence exists, how it is retrieved, and how the plan is run.
/// </summary>
/// <remarks>
/// The document is the deployment's, and it is data rather than code: a profile names the collections, data views and
/// fact scopes a turn may read, and a step list says what a run does. Nothing here executes anything.
/// </remarks>
public sealed record RunbookDocument
{
    /// <summary>Gets the document's API version, which the reader checks nothing about beyond carrying it.</summary>
    public string? ApiVersion { get; init; }

    /// <summary>
    /// Gets the document's kind, which has to be <c>Runbook</c>.
    /// </summary>
    /// <remarks>
    /// The kind exists so a document handed to the wrong reader is refused rather than half-understood: a shape or a
    /// provider configuration has the same YAML shape and no meaning here.
    /// </remarks>
    public string? Kind { get; init; }

    /// <summary>Gets the identity.</summary>
    public required RunbookMeta Metadata { get; init; }

    /// <summary>Gets the document's body.</summary>
    public required RunbookSpec Spec { get; init; }
}

/// <summary>
/// One compartmentalized collection a v2 runbook retrieves from.
/// </summary>
public sealed record CollectionSpec
{
    /// <summary>Gets the tenant-unique name, which the executor resolves or creates.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the shape that governs this collection's sources.</summary>
    public required string Shape { get; init; }

    /// <summary>Gets the access level a capability token has to dominate to search here.</summary>
    public int AccessLevel { get; init; }

    /// <summary>Gets the need-to-know tags a token has to carry, all of them.</summary>
    public IReadOnlyList<string> Compartments { get; init; } = [];

    /// <summary>Gets the declarative source binding, which every resolve step re-evaluates.</summary>
    public SourceBinding? Sources { get; init; }

    /// <summary>Gets the evidence labelling for this collection.</summary>
    public CollectionEvidence? Evidence { get; init; }
}

/// <summary>
/// Declarative matchers binding uploaded sources to a collection.
/// </summary>
/// <remarks>
/// All present matchers OR together with the explicit hash list; within the prefix and media matchers, both hold when
/// both are declared.
/// </remarks>
public sealed record SourceBinding
{
    /// <summary>Gets the path prefix a source has to sit under.</summary>
    public string? FilenamePrefix { get; init; }

    /// <summary>Gets the media types that bind.</summary>
    public IReadOnlyList<string> MediaTypes { get; init; } = [];

    /// <summary>Gets the content hashes that bind.</summary>
    public IReadOnlyList<string> ContentHashes { get; init; } = [];

    /// <summary>Gets a value indicating whether this binding matches nothing at all.</summary>
    public bool IsEmpty =>
        FilenamePrefix is not { Length: > 0 } && MediaTypes.Count == 0 && ContentHashes.Count == 0;
}

/// <summary>
/// Evidence labelling for a collection.
/// </summary>
public sealed record CollectionEvidence
{
    /// <summary>
    /// Gets the free labels stamped onto evidence sealed from this collection, so a report can group by them without
    /// re-deriving provenance.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];
}

/// <summary>
/// Where a runbook's documents live in object storage.
/// </summary>
/// <remarks>
/// Blobs are addressed by their logical path, which is the same string a collection's <see cref="SourceBinding"/> prefix
/// matches - so declaring the prefix here says "everything this runbook reads lives under <c>northgate/</c>", which is
/// checkable rather than a convention someone has to remember.
/// </remarks>
public sealed record SourcesSpec
{
    /// <summary>Gets the blob container, or <see langword="null"/> for the server's own.</summary>
    public string? Container { get; init; }

    /// <summary>Gets the path prefix every collection binding has to sit under.</summary>
    public string? Prefix { get; init; }
}

/// <summary>
/// How a v2 run plan is flattened across collections.
/// </summary>
public enum ExecutionOrder
{
    /// <summary>All of step one across collections, then all of step two, and so on.</summary>
    StepMajor = 0,

    /// <summary>One collection through every step before the next starts.</summary>
    CollectionMajor = 1,
}

/// <summary>
/// The plan-flattening declaration.
/// </summary>
/// <remarks>
/// This is a real operational lever rather than a style choice. Only <c>cutover</c> can pause a run, so under
/// <see cref="ExecutionOrder.StepMajor"/> the FIRST request executes resolve, build and verify for every collection
/// before it reaches any gate - fine for a data room, impossible for a 530 MB archive, because that whole build has to
/// fit inside one request and a client disconnect wedges the step. <see cref="ExecutionOrder.CollectionMajor"/> walks
/// one collection at a time, so each request builds exactly one collection and the approval gates chunk the work.
/// </remarks>
public sealed record ExecutionSpec
{
    /// <summary>Gets the declared order, which defaults to <see cref="ExecutionOrder.StepMajor"/>.</summary>
    public ExecutionOrder Order { get; init; } = ExecutionOrder.StepMajor;
}
