namespace Munarium.Evidence;

/// <summary>
/// What a hierarchy run reports while it runs.
/// </summary>
/// <remarks>
/// The kernel's own vocabulary, deliberately wire-agnostic: a caller may translate these into progress events on
/// its transport, or ignore them. Nothing about a run's <em>result</em> depends on a listener being present.
/// </remarks>
public readonly union HierarchyProgress(ProfileResolved, LayerStarted, SourceBound, LayerCompleted, CoverageReported);

/// <summary>The profile a turn resolved to, before any layer ran.</summary>
/// <param name="Profile">The profile's name.</param>
/// <param name="Layers">The layers that will run, in order.</param>
/// <param name="IntentKind">The intent's kind, when it had one.</param>
/// <param name="IntentExplicit">Whether the intent was supplied rather than modelled.</param>
public sealed record ProfileResolved(
    string Profile,
    IReadOnlyList<string> Layers,
    string? IntentKind,
    bool IntentExplicit);

/// <summary>A layer began.</summary>
/// <param name="Layer">The layer's name.</param>
/// <param name="Role">The weight its evidence carries.</param>
/// <param name="Requirement">Whether its evidence is required.</param>
public sealed record LayerStarted(string Layer, AnswerRole Role, LayerRequirement Requirement);

/// <summary>A provider was bound to a pinned source.</summary>
/// <param name="Layer">The layer's name.</param>
/// <param name="Source">The pinned source.</param>
/// <param name="Provider">The provider that claimed it.</param>
public sealed record SourceBound(string Layer, string Source, string Provider);

/// <summary>A layer finished, with what it produced.</summary>
/// <param name="Layer">The layer's name.</param>
/// <param name="Block">The block's kind.</param>
/// <param name="SupportsCompleteness">Whether the block permits a completeness claim.</param>
/// <param name="RefusalCode">The refusal's code, when the layer declined.</param>
/// <param name="ElapsedMilliseconds">How long the layer took.</param>
public sealed record LayerCompleted(
    string Layer,
    string Block,
    bool SupportsCompleteness,
    string? RefusalCode,
    long ElapsedMilliseconds);

/// <summary>What the run's blocks collectively permit, once every layer has run.</summary>
/// <param name="CompletenessAvailable">Whether any block permits a completeness claim.</param>
/// <param name="DisclosedConflicts">How many conflicts between layers will be disclosed.</param>
public sealed record CoverageReported(bool CompletenessAvailable, int DisclosedConflicts);
