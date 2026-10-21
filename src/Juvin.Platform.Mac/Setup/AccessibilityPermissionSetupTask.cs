using System.ComponentModel;
using System.Diagnostics;
using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Platform.Mac.Launcher;
using Juvin.Platform.Setup;

namespace Juvin.Platform.Mac.Setup;

/// <summary>
///     Asks for the Accessibility permission the global shortcut hook needs on macOS. The hook checks
///     the permission through a CoreGraphics preflight that can keep its first answer for the life of
///     the process, so a permission granted while Juvin runs needs a restart, which the row offers.
/// </summary>
public sealed class AccessibilityPermissionSetupTask : ISetupTask
{
    internal const string SettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    private readonly Localizer _localizer;
    private readonly IProcessRunner _runner;
    private readonly IAccessibilityTrust _trust;
    private readonly Func<CancellationToken, Task> _hotkeyRestart;
    private readonly Func<bool> _hotkeyListening;
    private readonly Func<bool> _hotkeyNeedsPermission;
    private readonly IAppRelauncher _relauncher;
    private readonly SemaphoreSlim _restartGate = new(1, 1);

    /// <param name="localizer">Supplies the row text.</param>
    /// <param name="runner">Opens System Settings at the Accessibility pane.</param>
    /// <param name="hotkeyRestart">Called while the permission is granted and the shortcut is not listening, so a hook that failed without it starts.</param>
    /// <param name="hotkeyListening">Whether the shortcut is already listening, in which case no restart is needed.</param>
    /// <param name="hotkeyNeedsPermission">Whether the shortcut last failed because macOS refused the permission; only then can a restart help.</param>
    /// <param name="requestExit">Ends the app after a relaunch is set up.</param>
    public AccessibilityPermissionSetupTask(
        Localizer localizer,
        IProcessRunner runner,
        Func<CancellationToken, Task> hotkeyRestart,
        Func<bool> hotkeyListening,
        Func<bool> hotkeyNeedsPermission,
        Action requestExit
    )
        : this(localizer, runner, new AccessibilityTrust(), hotkeyRestart, hotkeyListening, new MacAppRelauncher(requestExit), hotkeyNeedsPermission)
    {
    }

    internal AccessibilityPermissionSetupTask(
        Localizer localizer,
        IProcessRunner runner,
        IAccessibilityTrust trust,
        Func<CancellationToken, Task> hotkeyRestart,
        Func<bool> hotkeyListening,
        IAppRelauncher relauncher,
        Func<bool>? hotkeyNeedsPermission = null
    )
    {
        _localizer = localizer;
        _runner = runner;
        _trust = trust;
        _hotkeyRestart = hotkeyRestart;
        _hotkeyListening = hotkeyListening;
        _hotkeyNeedsPermission = hotkeyNeedsPermission ?? (() => true);
        _relauncher = relauncher;
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
        if (!_trust.IsTrusted())
        {
            return new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                _localizer["Setup.Accessibility.Missing"],
                _localizer["Setup.Accessibility.Detail"],
                _localizer["Setup.Accessibility.Action"]
            );
        }

        // On every look, not just when trust turns up: the hook may have failed at startup or on an earlier restart.
        if (!_hotkeyListening())
        {
            await RestartHotkeyAsync(ct).ConfigureAwait(false);
        }

        if (_hotkeyListening())
        {
            return new SetupTaskState(SetupTaskStatusKind.Satisfied, _localizer["Setup.Accessibility.Granted"]);
        }

        if (!_hotkeyNeedsPermission())
        {
            return new SetupTaskState(
                SetupTaskStatusKind.Failed,
                _localizer["Setup.Accessibility.NotListening"],
                _localizer["Setup.Accessibility.OtherFailureDetail"]
            );
        }

        return _relauncher.CanRelaunch
            ? new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                _localizer["Setup.Accessibility.NotListening"],
                _localizer["Setup.Accessibility.RestartDetail"],
                _localizer["Setup.Accessibility.RestartAction"]
            )
            : new SetupTaskState(
                SetupTaskStatusKind.Failed,
                _localizer["Setup.Accessibility.NotListening"],
                _localizer["Setup.Accessibility.NotListeningDetail"]
            );
    }

    private async Task RestartHotkeyAsync(CancellationToken ct)
    {
        // Overlapping checks restart once; the one that waited sees the result.
        await _restartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_hotkeyListening())
            {
                await _hotkeyRestart(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine($"[AccessibilityPermissionSetupTask] Hotkey restart failed ({ex.GetType().Name}).");
        }
        finally
        {
            _restartGate.Release();
        }
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        // A restart helps only once trusted; without trust the action still opens the settings.
        if (_trust.IsTrusted() && !_hotkeyListening() && _hotkeyNeedsPermission() && _relauncher.CanRelaunch)
        {
            return _relauncher.Relaunch()
                ? new SetupActionOutcome(true, _localizer["Setup.Accessibility.Restarting"])
                : new SetupActionOutcome(false, _localizer["Setup.Accessibility.RestartFailed"]);
        }

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
