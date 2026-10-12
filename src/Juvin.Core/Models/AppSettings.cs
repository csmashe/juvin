// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global -- init accessors are required for JSON deserialization and with-expressions
// ReSharper disable UnusedAutoPropertyAccessor.Global -- read or written by the JSON round-trip and the settings UI once it lands
// ReSharper disable UnusedMember.Global -- settings consumed by the app once the UI lands

using System.Collections.Immutable;

namespace Juvin.Core.Models;

/// <summary>
///     Immutable settings snapshot. Change it with <c>with</c> expressions through
///     <see cref="Interfaces.ISettingsService.Update" />.
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

    /// <summary>The id of the hotkey backend the user picked; null chooses the first available one.</summary>
    public string? HotkeyBackend { get; init; }

    /// <summary>The id of the action run as soon as a capture arrives.</summary>
    public string DefaultRewriteAction { get; init; } = "clearer";

    /// <summary>
    ///     Edited instructions for built-in actions, by action id; an absent id uses the built-in prompt.
    ///     A hand-edited file may hold null here or in <see cref="CustomRewriteActions" />; both read as empty.
    /// </summary>
    public IReadOnlyDictionary<string, string> RewriteActionPrompts
    {
        get;
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- System.Text.Json passes a JSON null through regardless of the annotation
        init => field = value ?? ImmutableDictionary<string, string>.Empty;
    } = ImmutableDictionary<string, string>.Empty;

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- System.Text.Json passes JSON nulls through regardless of the annotations
    // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    public IReadOnlyList<CustomRewriteAction> CustomRewriteActions
    {
        get;
        init =>
            field = value is null
                ? []
                : [
                    .. value
                        .Where(action => action is not null)
                        .Select(action => new CustomRewriteAction(action.Id ?? "", action.Name ?? "", action.Instruction ?? "")),
                ];
    } = [];
    // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    public LengthGuidance RewriteLengthGuidance { get; init; } = LengthGuidance.None;

    public bool HistoryEnabled { get; init; }
    public int HistoryRetentionDays { get; init; } = 30;
    public bool ProvenanceCaptureEnabled { get; init; }
    public bool CrashReportingEnabled { get; init; }

    public bool LaunchAtLogin { get; init; }
    public bool CloseToTray { get; init; } = true;
    public string? UiLanguage { get; init; }

    public bool HasCompletedOnboarding { get; init; }
}
