namespace Munarium.Core.Tests.Support;

using Munarium.Access;

/// <summary>
/// An audit held in a list, for the tests that need one without a database.
/// </summary>
/// <remarks>
/// It keeps the same rules the stored one does - a row per capability identity, and a first withdrawal that stands - so a
/// test that passes here is a statement about the seam rather than about this list.
/// </remarks>
public sealed class InMemoryAccessTokenAudit : IAccessTokenAudit
{
    private readonly List<IssuedCapability> _rows = [];

    /// <summary>Gets how many rows have been recorded.</summary>
    public int Count => _rows.Count;

    /// <inheritdoc />
    public ValueTask<IssuedCapability> RecordAsync(
        IssuedCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        cancellationToken.ThrowIfCancellationRequested();

        // One row per identity: recording the same capability twice is the same row, not two.
        _rows.RemoveAll(row => string.Equals(row.TokenId, capability.TokenId, StringComparison.Ordinal));
        _rows.Add(capability);

        return ValueTask.FromResult(capability);
    }

    /// <inheritdoc />
    public ValueTask<IssuedCapability?> GetAsync(
        string tenant,
        string tokenId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_rows.Find(row =>
            string.Equals(row.TokenId, tokenId, StringComparison.Ordinal)
            && string.Equals(row.Tenant, tenant, StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IssuedCapability>> ListAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<IssuedCapability>>(
            [.. _rows.Where(row => string.Equals(row.Tenant, tenant, StringComparison.Ordinal))]);
    }

    /// <inheritdoc />
    public ValueTask<IssuedCapability?> RevokeAsync(
        string tenant,
        string tokenId,
        long revokedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var index = _rows.FindIndex(row =>
            string.Equals(row.TokenId, tokenId, StringComparison.Ordinal)
            && string.Equals(row.Tenant, tenant, StringComparison.Ordinal));

        if (index < 0)
        {
            return ValueTask.FromResult<IssuedCapability?>(null);
        }

        // The first withdrawal is the one that stands, so withdrawing twice reports the first instant rather than moving
        // it: an operator acting on a stale list has done nothing new.
        var withdrawn = _rows[index] is { RevokedAt: null } row ? row with { RevokedAt = revokedAt } : _rows[index];
        _rows[index] = withdrawn;

        return ValueTask.FromResult<IssuedCapability?>(withdrawn);
    }
}
