namespace Munarium.Claims;

using Munarium.Facts;

/// <summary>
/// One claim a model proposed for a scope, before anything has been accepted.
/// </summary>
/// <remarks>
/// The proposal carries no identity and no sequence, because it does not exist in the ledger yet: a
/// gate judges a proposal, and only an accepted proposal becomes a <see cref="Claim"/>. Keeping the
/// two types apart is what stops a pre-acceptance value from being mistaken for ledger state.
/// </remarks>
public sealed record ProposedClaim
{
    /// <summary>
    /// Gets what the proposal would do to whatever the ledger holds on its claim key.
    /// </summary>
    /// <remarks>
    /// Which plane a proposal is judged on is decided by the list it travels in - claims for plain
    /// assertions, corrections for declared supersessions - so this field records the intent rather
    /// than routing the proposal.
    /// </remarks>
    public ClaimType ClaimType { get; init; } = ClaimType.Fact;

    /// <summary>Gets the subject - the thing the proposal is about.</summary>
    public required string Subject { get; init; }

    /// <summary>Gets the property of the subject the proposal is about.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the proposed value.</summary>
    public required string Value { get; init; }

    /// <summary>Gets the claim the proposal says it supersedes, when it says so.</summary>
    public string? SupersedesId { get; init; }

    /// <summary>Gets the scope the proposal was produced in, when it names one.</summary>
    /// <remarks>
    /// Per proposal rather than per batch, because the request carries it per claim and a batch may span
    /// scopes. The candidate's own scope - the one the gates report a finding against - is the first scope
    /// named by any proposal in the batch.
    /// </remarks>
    public string? ScopePath { get; init; }

    /// <summary>Gets how the proposal came to exist. See <see cref="Provenance"/>.</summary>
    public Provenance Provenance { get; init; } = Provenance.Witnessed;

    /// <summary>Gets the claim key: what the ledger matches the proposal against.</summary>
    public string ClaimKey => string.Concat(Subject, ".", Key);
}
