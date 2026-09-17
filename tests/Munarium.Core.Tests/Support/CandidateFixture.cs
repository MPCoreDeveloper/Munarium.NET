namespace Munarium.Core.Tests.Support;

using Munarium.Claims;
using Munarium.Facts;
using Munarium.Governance;

/// <summary>
/// Builds the write paths the governance tests drive: a candidate ledger over the in-memory store, and the
/// proposals they send through it.
/// </summary>
internal static class CandidateFixture
{
    /// <summary>Builds a candidate ledger over a fresh in-memory store.</summary>
    /// <returns>The ledger, the store it writes to, and the fact read model over it.</returns>
    internal static (CandidateLedger Ledger, FakeStorageBackend Storage, FactLedger Facts) Ledger()
    {
        var storage = new FakeStorageBackend();
        var facts = new FactLedger(storage);

        return (new CandidateLedger(storage, new MeshSnapshotBuilder(facts)), storage, facts);
    }

    /// <summary>Builds a proposal.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="key">The property.</param>
    /// <param name="value">The value.</param>
    /// <param name="claimType">What the proposal does to what the ledger holds.</param>
    /// <param name="scope">The scope, or <see langword="null"/> for none.</param>
    /// <returns>The proposal.</returns>
    internal static ProposedClaim Proposal(
        string subject,
        string key,
        string value,
        ClaimType claimType = ClaimType.Fact,
        string? scope = null) => new()
        {
            ClaimType = claimType,
            Subject = subject,
            Key = key,
            Value = value,
            ScopePath = scope,
        };
}
