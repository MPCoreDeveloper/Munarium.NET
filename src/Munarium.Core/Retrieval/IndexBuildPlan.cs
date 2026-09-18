namespace Munarium.Retrieval;

using Munarium.Ledger;

/// <summary>
/// What a build should produce, and over which sources.
/// </summary>
/// <remarks>
/// The binding is in the request rather than in the manifest, deliberately: the manifest records what a version
/// <em>indexed</em>, while which sources a collection <em>binds</em> is a decision that can change without the version
/// changing - and a version that recorded its binding would make a new binding look like a new corpus.
/// </remarks>
public sealed record IndexBuildPlan
{
    /// <summary>Gets the tenant whose sources are indexed.</summary>
    public required string Tenant { get; init; }

    /// <summary>Gets the collection the version belongs to.</summary>
    public required string CollectionId { get; init; }

    /// <summary>Gets the collection's name, so a manifest needs no second lookup to be read.</summary>
    public required string CollectionName { get; init; }

    /// <summary>Gets the contract shape the corpus is mapped through.</summary>
    public required string ShapeRef { get; init; }

    /// <summary>
    /// Gets the path prefix that binds the collection, or <see langword="null"/> for every source the tenant has.
    /// </summary>
    public string? PathPrefix { get; init; }

    /// <summary>Gets the ledger position the build reflects, which is where an answer's provenance ends.</summary>
    public required SequenceNumber Watermark { get; init; }

    /// <summary>
    /// Gets a value indicating whether the built version becomes the collection's live one.
    /// </summary>
    /// <remarks>
    /// Off by default, like the catalogue's own flag: a build nobody has inspected is not a build that should be
    /// answering questions.
    /// </remarks>
    public bool Activate { get; init; }
}

/// <summary>A build that could not be made, and why.</summary>
/// <param name="Reason">Why nothing was built.</param>
public sealed record IndexBuildRefused(string Reason);

/// <summary>The outcome of a build: the version as recorded, or the refusal that came first.</summary>
public readonly union IndexBuildOutcome(IndexVersion, IndexBuildRefused);
