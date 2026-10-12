using System.ComponentModel;
using System.Diagnostics;
using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Platform.Setup;

namespace Juvin.Platform.Mac.Setup;

/// <summary>
///     Asks for the Accessibility permission the global shortcut hook needs on macOS. Unverified on hardware.
/// </summary>
public sealed class AccessibilityPermissionSetupTask : ISetupTask
{
    internal const string SettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    private readonly Localizer _localizer;
    private readonly IProcessRunner _runner;
    private readonly IAccessibilityTrust _trust;
    private readonly Func<CancellationToken, Task> _hotkeyRestart;
    private bool? _lastTrusted;

    /// <param name="localizer">Supplies the row text.</param>
    /// <param name="runner">Opens System Settings at the Accessibility pane.</param>
    /// <param name="hotkeyRestart">Called once the permission turns up granted, so the hook that failed without it starts.</param>
    public AccessibilityPermissionSetupTask(Localizer localizer, IProcessRunner runner, Func<CancellationToken, Task> hotkeyRestart)
        : this(localizer, runner, new AccessibilityTrust(), hotkeyRestart)
    {
    }

    internal AccessibilityPermissionSetupTask(
        Localizer localizer,
        IProcessRunner runner,
        IAccessibilityTrust trust,
        Func<CancellationToken, Task> hotkeyRestart
    )
    {
        _localizer = localizer;
        _runner = runner;
        _trust = trust;
        _hotkeyRestart = hotkeyRestart;
    }

    public string Id => "accessibility";

    public string Title => _localizer["Setup.Accessibility.Title"];

    public SetupTaskSeverity Severity => SetupTaskSeverity.Recommended;

    public bool AppliesToThisMachine()
    {
        return OperatingSystem.IsMacOS();
    }

    public async Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        // Also on the first look: the hook may have failed at startup before the user granted trust.
        var trusted = _trust.IsTrusted();
        if (trusted && _lastTrusted != true)
        {
            await RestartHotkeyAsync(ct).ConfigureAwait(false);
        }

        _lastTrusted = trusted;
        return trusted
            ? new SetupTaskState(SetupTaskStatusKind.Satisfied, _localizer["Setup.Accessibility.Granted"])
            : new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                _localizer["Setup.Accessibility.Missing"],
                _localizer["Setup.Accessibility.Detail"],
                _localizer["Setup.Accessibility.Action"]
            );
    }

    private async Task RestartHotkeyAsync(CancellationToken ct)
    {
        try
        {
            await _hotkeyRestart(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine($"[AccessibilityPermissionSetupTask] Hotkey restart failed ({ex.GetType().Name}).");
        }
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        _trust.Prompt();
        try
        {
            var result = await _runner.RunAsync("open", [SettingsUrl], null, null, ct);
            return result.ExitCode == 0
                ? new SetupActionOutcome(true, _localizer["Setup.Accessibility.Opened"])
                : new SetupActionOutcome(false, _localizer["Setup.Accessibility.OpenFailed"]);
        }
        catch (Win32Exception)
        {
            return new SetupActionOutcome(false, _localizer["Setup.Accessibility.OpenFailed"]);
        }
    }
}
