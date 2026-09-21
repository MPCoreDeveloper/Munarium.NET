namespace Munarium.Providers;

/// <summary>
/// Where a deployment's applied provider declarations are kept.
/// </summary>
/// <remarks>
/// A seam rather than a dictionary in the registry, for the reason every other record here is one: a declaration a
/// restart forgets is a plane nobody can rely on, and a deployment that applies one tomorrow expects the deployment
/// that comes back to still know it.
/// </remarks>
public interface IProviderDeclarations
{
    /// <summary>Records a declaration, replacing the one of the same name.</summary>
    /// <param name="tenant">The tenant the declaration belongs to.</param>
    /// <param name="declaration">The declaration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The declaration as it now stands.</returns>
    ValueTask<ProviderDeclaration> SaveAsync(
        string tenant,
        ProviderDeclaration declaration,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one declaration by name.</summary>
    /// <param name="tenant">The tenant the declaration belongs to.</param>
    /// <param name="name">The name it was applied under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The declaration, or <see langword="null"/> when this deployment holds none by that name.</returns>
    ValueTask<ProviderDeclaration?> FindAsync(
        string tenant,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>This deployment's declarations, by name.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The declarations, ordered by name.</returns>
    ValueTask<IReadOnlyList<ProviderDeclaration>> ListAsync(
        string tenant,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The adapters a deployment holds, by family.
/// </summary>
/// <remarks>
/// Deliberately not a registry of credentials and not a vendor choice: the kernel never talks to a network, so a dialect
/// is served by an adapter the deployment composed - an <see cref="IModelProvider"/> - and a family with no adapter is a
/// family this deployment cannot answer for. That absence is a named, honest answer rather than an error, which is what
/// makes health over this seam worth serving.
/// </remarks>
public interface IProviderAdapters
{
    /// <summary>Finds the adapter this deployment holds for a family.</summary>
    /// <param name="family">The family a declaration speaks for.</param>
    /// <returns>The adapter, or <see langword="null"/> when this deployment holds none for that family.</returns>
    IModelProvider? AdapterFor(string family);
}

/// <summary>
/// The adapters a deployment composed, as one value.
/// </summary>
/// <param name="adapters">One adapter per family, keyed by the family's name.</param>
public sealed class ProviderAdapters(
    IEnumerable<KeyValuePair<string, IModelProvider>> adapters) : IProviderAdapters
{
    private readonly Dictionary<string, IModelProvider> _adapters = new(
        adapters ?? throw new ArgumentNullException(nameof(adapters)),
        StringComparer.Ordinal);

    /// <summary>No adapters at all, which is what a deployment composed only for its own corpus holds.</summary>
    public static ProviderAdapters None { get; } = new([]);

    /// <summary>Reads one adapter out of the composition.</summary>
    /// <param name="family">The family.</param>
    /// <returns>The adapter, or <see langword="null"/>.</returns>
    public IModelProvider? AdapterFor(string family) =>
        _adapters.TryGetValue(family, out var adapter) ? adapter : null;
}
