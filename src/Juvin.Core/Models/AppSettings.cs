// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global -- init accessors are required for JSON deserialization and with-expressions
// ReSharper disable UnusedAutoPropertyAccessor.Global -- read or written by the JSON round-trip and the settings UI once it lands
// ReSharper disable UnusedMember.Global -- settings consumed by the app once the UI lands

using System.Collections.Immutable;

namespace Juvin.Core.Models;

/// <summary>
///     Immutable settings snapshot. Change it with <c>with</c> expressions through
///     <see cref="Interfaces.ISettingsService.Update" />. Compare snapshots by their JSON, not
///     with <c>==</c>: the collection properties compare by reference.
/// </summary>
public sealed record AppSettings
{
    private const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ProcessingMode ProcessingMode { get; init; } = ProcessingMode.Local;
    public string LocalServerUrl { get; init; } = "http://localhost:11434";
    public string? LocalModel { get; init; }
    public bool LocalThinkingDisabled { get; init; } = true;
    public string? CodexBinaryPath { get; init; }
    public string? CodexModel { get; init; }

    public string RewriteShortcut { get; init; } = "Ctrl+Shift+F";

    /// <summary>
    ///     The key position the user pressed when recording the shortcut, as a canonical key name, when it differs
    ///     from the logical key in <see cref="RewriteShortcut" />; null means the same key.
    /// </summary>
    public string? RewriteShortcutPhysicalKey { get; init; }

    /// <summary>The id of the hotkey backend the user picked; null chooses the first available one.</summary>
    public string? HotkeyBackend { get; init; }

    /// <summary>The id of the action run as soon as a capture arrives.</summary>
    public string DefaultRewriteAction { get; init; } = "clearer";

    /// <summary>Edited instructions by built-in action id; an action with no entry uses its default.</summary>
    public IReadOnlyDictionary<string, string> RewriteInstructionOverrides
    {
        get;
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- the settings file can hold null
        init => field = value ?? ImmutableSortedDictionary<string, string>.Empty;
    } = ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>The user's own actions, in the order the panel lists them.</summary>
    public IReadOnlyList<CustomRewriteAction> CustomRewriteActions
    {
        get;
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- the settings file can hold null
        init => field = value ?? [];
    } = [];

    public bool HistoryEnabled { get; init; }
    public int HistoryRetentionDays { get; init; } = 30;
    public bool ProvenanceCaptureEnabled { get; init; }
    public bool CrashReportingEnabled { get; init; }

    public bool LaunchAtLogin { get; init; }
    public bool CloseToTray { get; init; } = true;
    public string? UiLanguage { get; init; }

    public bool HasCompletedOnboarding { get; init; }
}
