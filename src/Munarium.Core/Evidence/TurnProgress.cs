namespace Munarium.Evidence;

/// <summary>
/// What a turn reports while it runs.
/// </summary>
/// <remarks>
/// The kernel's own vocabulary, deliberately wire-agnostic: a caller may translate these into progress events on its
/// transport, or ignore them, and nothing about a turn's <em>result</em> depends on a listener being present.
/// <para>
/// The hierarchy's stages are a union of their own (<see cref="HierarchyProgress"/>) and stay one. A turn runs a
/// hierarchy in the middle of its own stages, and a single flat list of events would make the hierarchy look like it
/// owns the retrieval and the completion, which it does not. A transport that needs one shape on the wire flattens the
/// two, which is a translation rather than a change of meaning.
/// </para>
/// <para>
/// A listener is called at the boundary it reports and is expected not to throw: the events describe the turn rather
/// than produce it, so an observer that cannot keep up is the observer's problem. A transport whose listener can fail
/// for a reason that has nothing to do with the turn - a client that hung up mid-stream - handles that inside its own
/// listener, because a turn that has already been paid for still has to be recorded.
/// </para>
/// </remarks>
public readonly union TurnProgress(
    HierarchyProgress,
    TurnModelResolved,
    TurnMerged,
    TurnComposed,
    TurnCompleted,
    TurnVerified);

/// <summary>The models the turn resolved for its paid steps, before any of them is paid for.</summary>
/// <param name="Provider">The provider dialect that will answer.</param>
/// <param name="Model">The model the deployment resolved, as the provider names it.</param>
/// <param name="Tier">The tier it resolved through, when it resolved a tier rather than a model.</param>
/// <param name="WasOverride">Whether the caller asked for it rather than the runbook.</param>
public sealed record TurnModelResolved(string Provider, string Model, string? Tier, bool WasOverride);

/// <summary>The retrieval the turn's evidence came from returned.</summary>
/// <remarks>
/// One event for the turn rather than one per collection, and that is a property of this port rather than a
/// simplification: it serves one index across every collection a session may read, so there is no per-collection
/// result to report, and a collection's name beside a count nobody measured would be an invented measurement.
/// </remarks>
/// <param name="Hits">How many chunks the merged result carried.</param>
public sealed record TurnMerged(int Hits);

/// <summary>The evidence's blocks were composed into the context the model is given.</summary>
/// <param name="LayersUsed">How many blocks went into the context.</param>
/// <param name="ContextCharacters">How many characters that context came to.</param>
/// <param name="LayersDropped">The layers that did not fit the budget.</param>
public sealed record TurnComposed(int LayersUsed, int ContextCharacters, IReadOnlyList<string> LayersDropped);

/// <summary>One paid completion returned.</summary>
/// <param name="Attempt">Zero for the answer and its truncation re-ask, one upward for each corrective retry.</param>
/// <param name="Provider">The provider dialect that answered.</param>
/// <param name="Model">The model that actually answered.</param>
/// <param name="InputTokens">What the call cost to send.</param>
/// <param name="OutputTokens">What the call cost to generate.</param>
public sealed record TurnCompleted(int Attempt, string Provider, string Model, int InputTokens, int OutputTokens);

/// <summary>Deterministic verification ran over the current answer.</summary>
/// <remarks>
/// No layer, deliberately: this port checks an answer against everything the turn served it - the composed context and
/// the documents its layers produced - rather than re-checking it per layer, so there is no layer this event could
/// truthfully name.
/// </remarks>
/// <param name="Attempt">The attempt the checks ran over, numbered as the completion events number theirs.</param>
/// <param name="Checks">The checks that ran, in the order a report lists them.</param>
/// <param name="Violations">How many violations they found.</param>
public sealed record TurnVerified(int Attempt, IReadOnlyList<string> Checks, int Violations);
