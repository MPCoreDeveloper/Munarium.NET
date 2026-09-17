namespace Munarium.Core.Tests.Support;

using Munarium.Claims;
using Munarium.Facts;
using Munarium.Ledger;

/// <summary>
/// Builds the claim-model fixtures the kernel tests share: claims, proposals and the pinned snapshot
/// they are judged against.
/// </summary>
/// <remarks>
/// One place for the defaults, so a test that says nothing about scope or status gets the same claim as
/// every other test that says nothing - and a change to the fixture is visible in one diff rather than
/// scattered through the suites.
/// </remarks>
internal static class ClaimFixture
{
    /// <summary>The version every fixture claim belongs to.</summary>
    internal const string VersionId = "v1";

    /// <summary>The scope every fixture claim is written in unless a test says otherwise.</summary>
    internal const string Scope = "ch1";

    /// <summary>Builds a claim.</summary>
    /// <param name="id">The claim's identity.</param>
    /// <param name="sequence">The claim's ledger position.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="key">The property.</param>
    /// <param name="value">The value.</param>
    /// <param name="supersedes">The claim this one supersedes, when it does.</param>
    /// <param name="scope">The scope, or <see langword="null"/> for none.</param>
    /// <param name="status">The status.</param>
    /// <returns>The claim.</returns>
    internal static Claim Create(
        string id,
        long sequence,
        string subject,
        string key,
        string value,
        string? supersedes = null,
        string? scope = Scope,
        ClaimStatus status = ClaimStatus.Accepted) => new()
        {
            Id = id,
            VersionId = VersionId,
            Sequence = new SequenceNumber(sequence),
            ClaimType = supersedes is null ? ClaimType.Fact : ClaimType.Correction,
            Subject = subject,
            Key = key,
            Value = value,
            ScopePath = scope,
            Status = status,
            SupersedesId = supersedes,
        };

    /// <summary>Builds a proposed claim.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="key">The property.</param>
    /// <param name="value">The value.</param>
    /// <param name="supersedes">The claim the proposal says it supersedes, when it does.</param>
    /// <returns>The proposal.</returns>
    internal static ProposedClaim Propose(string subject, string key, string value, string? supersedes = null) => new()
    {
        Subject = subject,
        Key = key,
        Value = value,
        SupersedesId = supersedes,
    };

    /// <summary>Builds a snapshot holding the given facts.</summary>
    /// <param name="facts">The current facts, in ascending sequence order.</param>
    /// <returns>The snapshot.</returns>
    internal static MeshSnapshot Snapshot(params Claim[] facts) => new()
    {
        VersionId = VersionId,
        Facts = facts,
    };

    /// <summary>Builds a snapshot holding facts and locked anchors.</summary>
    /// <param name="facts">The current facts.</param>
    /// <param name="anchors">The locked anchors.</param>
    /// <returns>The snapshot.</returns>
    internal static MeshSnapshot SnapshotWithAnchors(IReadOnlyList<Claim> facts, params Anchor[] anchors) => new()
    {
        VersionId = VersionId,
        Facts = facts,
        Anchors = anchors.ToDictionary(anchor => anchor.DetailKey, StringComparer.Ordinal),
    };

    /// <summary>Builds a locked anchor.</summary>
    /// <param name="detailKey">The locked detail, as <c>subject.key</c>.</param>
    /// <param name="lockedValue">The value it is locked to.</param>
    /// <param name="sequence">The position the lock was taken at.</param>
    /// <returns>The anchor.</returns>
    internal static Anchor Lock(string detailKey, string lockedValue, long sequence = 1) => new()
    {
        Id = $"anchor-{detailKey}",
        VersionId = VersionId,
        DetailKey = detailKey,
        LockedValue = lockedValue,
        Sequence = new SequenceNumber(sequence),
    };
}
