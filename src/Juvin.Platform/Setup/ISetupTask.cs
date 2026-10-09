namespace Juvin.Platform.Setup;

/// <summary>
///     Importance of a setup task. <see cref="Required" /> tasks gate the wizard's Finish button;
///     <see cref="Recommended" /> tasks appear in the checklist but never block completion.
/// </summary>
public enum SetupTaskSeverity
{
    Required,
    Recommended,
}

/// <summary>
///     The evaluated state of a setup task on this machine.
/// </summary>
public enum SetupTaskStatusKind
{
    /// <summary>The capability is present and working.</summary>
    Satisfied,

    /// <summary>The task needs an action (auto-fix, open-a-page, or a manual command).</summary>
    NeedsAction,

    /// <summary>An action is currently running.</summary>
    Working,

    /// <summary>The last action failed; the user can retry or fall back to the manual command.</summary>
    Failed,
}

/// <summary>A task's current state as rendered by the wizard.</summary>
/// <param name="Kind">Drives the status badge and Finish gating.</param>
/// <param name="Summary">One-line description of the current state.</param>
/// <param name="Detail">Optional longer explanation of what the fix will do.</param>
/// <param name="ActionLabel">Action button label; null when there is no automatable action.</param>
/// <param name="CopyCommand">Shell command shown when the fix cannot be fully automated.</param>
public sealed record SetupTaskState(
    SetupTaskStatusKind Kind,
    string Summary,
    string? Detail = null,
    string? ActionLabel = null,
    string? CopyCommand = null
);

/// <summary>
///     Outcome of running a task's action. <see cref="Success" /> means the action completed
///     without error, not that the task is now satisfied; the caller re-evaluates to learn that.
/// </summary>
// ReSharper disable once NotAccessedPositionalProperty.Global -- read once setup tasks report real outcomes
public sealed record SetupActionOutcome(bool Success, string Message, string? Detail = null);

/// <summary>
///     One unit of machine setup driven by the onboarding wizard. Each task owns one capability
///     (hotkey access, browser connector, OS permission, ...), decides whether it applies to the
///     current machine, reports its own status, and performs its own fix. Adding support for a new
///     desktop environment or OS means registering tasks, not editing the wizard.
/// </summary>
public interface ISetupTask
{
    /// <summary>Stable identifier used in logs and as the row key.</summary>
    // ReSharper disable once UnusedMemberInSuper.Global -- row key for the wizard once tasks exist
    string Id { get; }

    /// <summary>Human-readable title shown as the checklist row heading.</summary>
    string Title { get; }

    /// <summary>Whether this task blocks completion when unsatisfied.</summary>
    SetupTaskSeverity Severity { get; }

    /// <summary>
    ///     Cheap, synchronous gate: tasks that do not apply are omitted from the checklist.
    ///     Must only read environment variables and look up binaries, never spawn commands.
    /// </summary>
    bool AppliesToThisMachine();

    /// <summary>Evaluates the task's current state. Keep it quick; all tasks are evaluated together.</summary>
    // ReSharper disable once UnusedParameter.Global -- contract; the real tasks honour it
    Task<SetupTaskState> EvaluateAsync(CancellationToken ct);

    /// <summary>Performs the fix and returns the immediate outcome; the caller re-evaluates afterward.</summary>
    // ReSharper disable once UnusedParameter.Global -- contract; the real tasks honour it
    Task<SetupActionOutcome> RunActionAsync(CancellationToken ct);
}