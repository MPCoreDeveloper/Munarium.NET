namespace Munarium.Runbooks;

/// <summary>
/// The GA step vocabulary: the reindex pipeline, in the order a run executes it.
/// </summary>
public enum StepKind
{
    /// <summary>Count the sources bound to the collection, after syncing its declared source binding.</summary>
    ResolveSources = 0,

    /// <summary>Build the index side by side; never activates one, because cutover does that.</summary>
    BuildIndex = 1,

    /// <summary>Verify the built, still inactive index deterministically.</summary>
    Verify = 2,

    /// <summary>
    /// Check every declared data view: the contract exists, is verified, and its declared columns match.
    /// </summary>
    /// <remarks>
    /// Read-only, and deliberately a step rather than an apply-time check: it needs the semantic plane to be reachable,
    /// and applying a runbook must not depend on a second service being up.
    /// </remarks>
    VerifyDataViews = 3,

    /// <summary>Flip the active pointer atomically; an approval pauses the run before it.</summary>
    Cutover = 4,

    /// <summary>Drop chunk data for the versions beyond what is kept, leaving their manifests.</summary>
    RetireOld = 5,
}

/// <summary>
/// One step of a run.
/// </summary>
/// <remarks>
/// The original models this as an enum with per-case fields; this port keeps one record with a kind and the two fields
/// the vocabulary carries, because the set is closed and six small cases do not earn six types. What has to be faithful
/// is the name a step reports and whether it pauses the run, and both are.
/// </remarks>
public sealed record RunbookStep
{
    /// <summary>Gets which step this is.</summary>
    public required StepKind Kind { get; init; }

    /// <summary>Gets the approval a cutover declares, when it declares one.</summary>
    public string? Approval { get; init; }

    /// <summary>Gets how many versions a retire keeps.</summary>
    public int KeepVersions { get; init; } = 2;

    /// <summary>Gets the name the step reports, which is the name the profile vocabulary uses.</summary>
    public string Name => Kind switch
    {
        StepKind.ResolveSources => "resolveSources",
        StepKind.BuildIndex => "buildIndex",
        StepKind.Verify => "verify",
        StepKind.VerifyDataViews => "verifyDataViews",
        StepKind.Cutover => "cutover",
        _ => "retireOld",
    };

    /// <summary>Gets a value indicating whether the run pauses here for an approval.</summary>
    public bool RequiresApproval => Kind == StepKind.Cutover && string.Equals(Approval, "required", StringComparison.Ordinal);
}

/// <summary>
/// Where a step is in its run.
/// </summary>
public enum StepState
{
    /// <summary>Not started.</summary>
    Pending = 0,

    /// <summary>Running now.</summary>
    Running = 1,

    /// <summary>Paused for the approval it declares.</summary>
    AwaitingApproval = 2,

    /// <summary>Finished.</summary>
    Done = 3,

    /// <summary>Failed, and the run stops unless it resumes.</summary>
    Failed = 4,
}

/// <summary>
/// The names <see cref="StepState"/> travels under.
/// </summary>
public static class StepStateNames
{
    /// <summary>Returns the name the wire carries for a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The snake_case name.</returns>
    public static string ToWireName(this StepState state) => state switch
    {
        StepState.Pending => "pending",
        StepState.Running => "running",
        StepState.AwaitingApproval => "awaiting_approval",
        StepState.Done => "done",
        _ => "failed",
    };
}
