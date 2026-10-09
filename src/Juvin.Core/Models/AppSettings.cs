// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global -- init accessors are required for JSON deserialization and with-expressions
// ReSharper disable UnusedAutoPropertyAccessor.Global -- read or written by the JSON round-trip and the settings UI once it lands
// ReSharper disable UnusedMember.Global -- settings consumed by the app once the UI lands

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

    public string RewriteShortcut { get; init; } = "Ctrl+Shift+Y";

    public bool HistoryEnabled { get; init; }
    public int HistoryRetentionDays { get; init; } = 30;
    public bool ProvenanceCaptureEnabled { get; init; }
    public bool CrashReportingEnabled { get; init; }

    public bool LaunchAtLogin { get; init; }
    public bool CloseToTray { get; init; } = true;
    public string? UiLanguage { get; init; }

    public bool HasCompletedOnboarding { get; init; }
}
