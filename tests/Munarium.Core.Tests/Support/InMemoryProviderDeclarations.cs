namespace Munarium.Core.Tests.Support;

using Munarium.Providers;

/// <summary>
/// The provider declarations a deployment applied, held in memory for a test.
/// </summary>
/// <remarks>
/// The seam rather than a real table, because what these tests are about is the plane's rules - a reserved name, a tier
/// that resolves, a probe that says what is missing - and not the engine's storage. The table's own behaviour is tested
/// where the table is, in the SharpCoreDB suite.
/// </remarks>
internal sealed class InMemoryProviderDeclarations : IProviderDeclarations
{
    private readonly Dictionary<string, ProviderDeclaration> _held = [];

    /// <inheritdoc />
    public ValueTask<ProviderDeclaration> SaveAsync(
        string tenant,
        ProviderDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        _held[declaration.Name] = declaration;

        return ValueTask.FromResult(declaration);
    }

    /// <inheritdoc />
    public ValueTask<ProviderDeclaration?> FindAsync(
        string tenant,
        string name,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_held.TryGetValue(name, out var declaration) ? declaration : null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ProviderDeclaration>> ListAsync(
        string tenant,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<ProviderDeclaration>>(
            [.. _held.Values.OrderBy(declaration => declaration.Name, StringComparer.Ordinal)]);
}
