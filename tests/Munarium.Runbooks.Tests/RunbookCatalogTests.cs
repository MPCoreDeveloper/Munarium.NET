namespace Munarium.Runbooks.Tests;

using Munarium.Runbooks;
using Munarium.Runbooks.Tests.Support;
using Munarium.Sessions;

/// <summary>
/// Tests for applying a runbook: what is stored, what is refused, and what a name resolves back into.
/// </summary>
public class RunbookCatalogTests
{
    /// <summary>
    /// The worked example applies, and what it stores is the reference the pin is spelled with and the document a turn
    /// will read - which resolving the name gives back.
    /// </summary>
    [Fact]
    public async Task TheWorkedExampleAppliesAndResolvesBackIntoItsDocument()
    {
        var store = new MemoryRunbooks();

        var applied = Applied(await RunbookCatalog.ApplyAsync(store, "acme", Examples.Runbook()));

        Assert.Equal(SessionCreation.Pin(applied.Document), applied.Record.Ref);
        Assert.Contains("@", applied.Record.Ref, StringComparison.Ordinal);
        Assert.Equal(Examples.Runbook(), applied.Record.Yaml);
        Assert.NotNull(applied.Record.CreatedAt);
        Assert.Equal(RunbookStatus.Active, applied.Record.Status);

        var resolved = await RunbookCatalog.ResolveAsync(store, "acme", applied.Document.Metadata.Name);

        Assert.NotNull(resolved);
        Assert.Equal(applied.Record.Ref, resolved.Record.Ref);
        Assert.Equal(applied.Document.Metadata.Name, resolved.Document.Metadata.Name);
        Assert.Equal(applied.Document.Metadata.Version, resolved.Document.Metadata.Version);

        // A reference resolves to exactly that version; something nobody applied resolves to nothing, and another tenant
        // sees none of it.
        Assert.Equal(
            applied.Record.Ref,
            (await RunbookCatalog.ResolveAsync(store, "acme", applied.Record.Ref))?.Record.Ref);
        Assert.Null(await RunbookCatalog.ResolveAsync(store, "acme", "nobody-applied-this"));
        Assert.Null(await RunbookCatalog.ResolveAsync(store, "other", applied.Document.Metadata.Name));
    }

    /// <summary>
    /// A document that cannot be read is refused with the reader's own complaint, and nothing is stored: a half-read
    /// document would be configuration nobody wrote.
    /// </summary>
    [Fact]
    public async Task ADocumentThatCannotBeReadIsRefusedAndNothingIsStored()
    {
        var store = new MemoryRunbooks();

        var refusal = Refused(await RunbookCatalog.ApplyAsync(store, "acme", "kind: Something\napiVersion: v2"));

        Assert.Equal(RunbookRefusalCodes.Invalid, refusal.Code);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
        Assert.Empty(store.Stored);
    }

    /// <summary>
    /// A version that was removed is refused rather than resurrected, and the message tells the operator what to do
    /// instead - which is publish a new version, so the earlier turn records keep naming the document they ran on.
    /// </summary>
    [Fact]
    public async Task ARemovedVersionIsRefusedWithWhatToDoInstead()
    {
        var store = new MemoryRunbooks();
        var applied = Applied(await RunbookCatalog.ApplyAsync(store, "acme", Examples.Runbook()));

        Assert.True(await store.RemoveAsync("acme", applied.Record.Ref));

        var refusal = Refused(await RunbookCatalog.ApplyAsync(store, "acme", Examples.Runbook()));

        Assert.Equal(RunbookRefusalCodes.Removed, refusal.Code);
        Assert.Contains($"runbook '{applied.Record.Ref}' was removed", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("publish a new version instead", refusal.Message, StringComparison.Ordinal);

        // And it stays unresolvable, because a removed version is not a document a turn may read.
        Assert.Null(await RunbookCatalog.ResolveAsync(store, "acme", applied.Document.Metadata.Name));
    }

    private static RunbookApplied Applied(RunbookApplication application) =>
        application is RunbookApplied applied
            ? applied
            : throw new InvalidOperationException("The runbook was refused.");

    private static RunbookRefusal Refused(RunbookApplication application) =>
        application is RunbookRefusal refusal
            ? refusal
            : throw new InvalidOperationException("The runbook was applied.");

    /// <summary>The seam in memory: the store's own semantics are covered where the real one lives.</summary>
    private sealed class MemoryRunbooks : IRunbookStore
    {
        private readonly Dictionary<string, RunbookRecord> _stored = new(StringComparer.Ordinal);

        public List<RunbookRecord> Stored => [.. _stored.Values];

        public ValueTask<RunbookRecord> ApplyAsync(RunbookRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stored = record with
            {
                Status = RunbookStatus.Active,
                RemovalId = null,
                CreatedAt = _stored.TryGetValue(record.Ref, out var existing)
                    ? existing.CreatedAt
                    : "2026-09-18T00:00:00Z",
                UpdatedAt = "2026-09-18T00:00:01Z",
            };

            _stored[record.Ref] = stored;

            return ValueTask.FromResult(stored);
        }

        public ValueTask<RunbookRecord?> GetAsync(
            string tenant,
            string runbookRef,
            bool includeRemoved = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var found = _stored.GetValueOrDefault(runbookRef);

            return ValueTask.FromResult(
                found is null
                || !string.Equals(found.Tenant, tenant, StringComparison.Ordinal)
                || (!includeRemoved && found.Status is RunbookStatus.Removed)
                    ? null
                    : found);
        }

        public ValueTask<RunbookRecord?> ResolveAsync(
            string tenant,
            string nameOrRef,
            bool includeRemoved = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (nameOrRef.Contains('@', StringComparison.Ordinal))
            {
                return GetAsync(tenant, nameOrRef, includeRemoved, cancellationToken);
            }

            var newest = _stored.Values
                .Where(record => string.Equals(record.Tenant, tenant, StringComparison.Ordinal))
                .Where(record => includeRemoved || record.Status is not RunbookStatus.Removed)
                .Where(record => string.Equals(record.Name, nameOrRef, StringComparison.Ordinal))
                .OrderByDescending(record => record.Version ?? 0)
                .FirstOrDefault();

            return ValueTask.FromResult(newest);
        }

        public ValueTask<IReadOnlyList<RunbookRecord>> ListAsync(
            string tenant,
            bool includeRemoved = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<IReadOnlyList<RunbookRecord>>(
                [
                    .. _stored.Values
                        .Where(record => includeRemoved || record.Status is not RunbookStatus.Removed)
                        .OrderBy(record => record.Name, StringComparer.Ordinal)
                        .ThenBy(record => record.Version ?? 0),
                ]);
        }

        public ValueTask<RunbookRecord?> RequestRemovalAsync(
            string tenant,
            string runbookRef,
            string removalId,
            string at,
            string? by,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_stored.GetValueOrDefault(runbookRef) is not { Status: not RunbookStatus.Removed } record)
            {
                return ValueTask.FromResult<RunbookRecord?>(null);
            }

            _stored[runbookRef] = record with
            {
                Status = RunbookStatus.RemoveRequested,
                RemovalId = removalId,
                RemovalRequestedAt = at,
                RemovalRequestedBy = by,
            };

            return ValueTask.FromResult<RunbookRecord?>(_stored[runbookRef]);
        }

        public ValueTask<RunbookRecord?> ConfirmRemovalAsync(
            string tenant,
            string runbookRef,
            string removalId,
            string at,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_stored.GetValueOrDefault(runbookRef) is not
                { Status: RunbookStatus.RemoveRequested, RemovalId: { } armed } record
                || !string.Equals(armed, removalId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<RunbookRecord?>(null);
            }

            _stored[runbookRef] = record with { Status = RunbookStatus.Removed, RemovedAt = at };

            return ValueTask.FromResult<RunbookRecord?>(_stored[runbookRef]);
        }

        /// <summary>Removes a version the way a confirmed removal would, for a test that is not about the two passes.</summary>
        /// <param name="tenant">The tenant.</param>
        /// <param name="runbookRef">The reference.</param>
        /// <returns>Whether something was removed.</returns>
        public async ValueTask<bool> RemoveAsync(string tenant, string runbookRef)
        {
            await RequestRemovalAsync(tenant, runbookRef, "rm-test", "2026-09-18T00:00:02Z", "ada")
                .ConfigureAwait(false);

            return await ConfirmRemovalAsync(tenant, runbookRef, "rm-test", "2026-09-18T00:00:03Z")
                .ConfigureAwait(false) is not null;
        }
    }
}
