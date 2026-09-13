namespace Munarium.Retrieval;

using Munarium.Ledger;

/// <summary>
/// The provenance envelope every retrieval answer carries.
/// </summary>
/// <remarks>
/// This is the part that makes an answer checkable later: which index version produced it, which
/// ledger position it reflects, and exactly which source documents it used. The design keeps old
/// index manifests resolvable, so an envelope issued today can still be verified long after a
/// re-index has cut over.
/// </remarks>
/// <param name="IndexVersion">The immutable index version the answer came from.</param>
/// <param name="LedgerWatermark">
/// The ledger position the index reflects. Together with <paramref name="IndexVersion"/> this is
/// what lets a past answer be reproduced rather than merely believed.
/// </param>
/// <param name="Sources">The sources the answer actually used, in answer order.</param>
public sealed record ProvenanceEnvelope(
    string IndexVersion,
    SequenceNumber LedgerWatermark,
    IReadOnlyList<SourceReference> Sources);
