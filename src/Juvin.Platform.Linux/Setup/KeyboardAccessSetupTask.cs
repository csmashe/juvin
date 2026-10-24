using System.ComponentModel;
using System.Diagnostics;
using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Platform.Linux.Hotkeys.Evdev;
using Juvin.Platform.Setup;

namespace Juvin.Platform.Linux.Setup;

/// <summary>
///     Lets the global shortcut read keyboards on Wayland by installing a udev rule that tags them
///     <c>uaccess</c>, which logind turns into an ACL for the signed-in user right away. X11 does not
///     need it: the hook backend works there.
/// </summary>
public sealed class KeyboardAccessSetupTask : ISetupTask
{
    public const string RulePath = "/etc/udev/rules.d/70-juvin-input.rules";

    private const string RuleHeader = "# Juvin: lets the signed-in user read keyboards for its global shortcut.";

    private const string Rule =
        "SUBSYSTEM==\"input\", ENV{ID_INPUT_KEYBOARD}==\"1\", TAG+=\"uaccess\", GROUP=\"input\", MODE=\"0660\"";

    // pkexec's exit code when the user dismisses the password prompt.
    private const int PkexecDismissed = 126;

    // pkexec's exit code when no approval could be obtained, such as on a desktop with no
    // authentication agent to show the password prompt (Sway, most tiling window managers).
    private const int PkexecNotAuthorized = 127;

    // LaunchScript's exit code when pkexec is not installed.
    private const int PkexecMissing = 99;

    // .NET raises the open-file limit to the hard maximum, which children inherit, and pkexec marks every
    // descriptor up to that limit close-on-exec one call at a time: with systemd's limit of about a billion
    // it spins for over an hour after the password is accepted. 1024 is the usual default.
    private const string LaunchScript = "command -v pkexec > /dev/null || exit 99; ulimit -n 1024 2> /dev/null; exec pkexec \"$@\"";

    // Arguments arrive as $1 and $2, so the rule text needs no shell quoting. Any failed step
    // exits 1, so 126 and 127 can only come from pkexec itself.
    private const string InstallScript =
        "printf '%s\\n' \"$1\" \"$2\" > \"$3\" && chmod 0644 \"$3\" "
        + "&& udevadm control --reload-rules && udevadm trigger --subsystem-match=input && udevadm settle --timeout=5 "
        + "|| exit 1";

    private readonly Func<CancellationToken, Task> _accessGranted;
    private readonly Func<string, string?> _getEnvironment;
    private readonly Func<bool> _hasKeyboardAccess;
    private readonly Func<bool?> _inInputGroup;
    private readonly Localizer _localizer;
    private readonly IProcessRunner _processRunner;

    /// <param name="localizer">Supplies the row text.</param>
    /// <param name="processRunner">Runs pkexec to install the rule.</param>
    /// <param name="accessGranted">Called after the rule is installed, so the app can restart the shortcut on the evdev backend.</param>
    public KeyboardAccessSetupTask(Localizer localizer, IProcessRunner processRunner, Func<CancellationToken, Task> accessGranted)
        : this(
            localizer,
            processRunner,
            accessGranted,
            InputDeviceAccessCheck.HasKeyboardAccess,
            InputGroupCheck.CurrentUserInInputGroup,
            Environment.GetEnvironmentVariable
        )
    {
    }

    internal KeyboardAccessSetupTask(
        Localizer localizer,
        IProcessRunner processRunner,
        Func<CancellationToken, Task> accessGranted,
        Func<bool> hasKeyboardAccess,
        Func<bool?> inInputGroup,
        Func<string, string?> getEnvironment
    )
    {
        _localizer = localizer;
        _processRunner = processRunner;
        _accessGranted = accessGranted;
        _hasKeyboardAccess = hasKeyboardAccess;
        _inInputGroup = inInputGroup;
        _getEnvironment = getEnvironment;
    }

    /// <summary>What to run in a terminal instead of the automatic action.</summary>
    public static string ManualCommand =>
        $"printf '%s\\n' '{RuleHeader}' '{Rule}' | sudo tee {RulePath} > /dev/null "
        + "&& sudo udevadm control --reload-rules && sudo udevadm trigger --subsystem-match=input";

    public string Id => "keyboard-access";

    public string Title => _localizer["Setup.KeyboardAccess.Title"];

    public SetupTaskSeverity Severity => SetupTaskSeverity.Recommended;

    public bool AppliesToThisMachine() =>
        OperatingSystem.IsLinux()
        && (string.Equals(_getEnvironment("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(_getEnvironment("WAYLAND_DISPLAY")));

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        if (_hasKeyboardAccess())
        {
            return Task.FromResult(new SetupTaskState(SetupTaskStatusKind.Satisfied, _localizer["Setup.KeyboardAccess.Satisfied"]));
        }

        var detail = _localizer["Setup.KeyboardAccess.Detail"];
        if (_inInputGroup() == true)
        {
            detail += " " + _localizer["Setup.KeyboardAccess.SignOutHint"];
        }

        return Task.FromResult(
            new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                _localizer["Setup.KeyboardAccess.NeedsAction"],
                detail,
                _localizer["Setup.KeyboardAccess.Action"],
                ManualCommand
            )
        );
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        int exitCode;
        try
        {
            exitCode = (await _processRunner
                .RunAsync("/bin/sh", ["-c", LaunchScript, "sh", "/bin/sh", "-c", InstallScript, "sh", RuleHeader, Rule, RulePath], null, null, ct)
                .ConfigureAwait(false)).ExitCode;
        }
        catch (Win32Exception ex)
        {
            Trace.WriteLine($"[KeyboardAccessSetupTask] pkexec could not start ({ex.NativeErrorCode}).");
            return new SetupActionOutcome(false, _localizer["Setup.KeyboardAccess.NoPkexec"]);
        }

        if (exitCode == PkexecMissing)
        {
            Trace.WriteLine("[KeyboardAccessSetupTask] pkexec is not installed.");
            return new SetupActionOutcome(false, _localizer["Setup.KeyboardAccess.NoPkexec"]);
        }

        if (exitCode != 0)
        {
            Trace.WriteLine($"[KeyboardAccessSetupTask] Installing the rule exited with {exitCode}.");
            return new SetupActionOutcome(
                false,
                exitCode switch
                {
                    PkexecDismissed => _localizer["Setup.KeyboardAccess.Cancelled"],
                    PkexecNotAuthorized => _localizer["Setup.KeyboardAccess.NotAuthorized"],
                    _ => _localizer.Format("Setup.KeyboardAccess.Failed", exitCode),
                }
            );
        }

        Trace.WriteLine("[KeyboardAccessSetupTask] Installed the keyboard rule.");
        try
        {
            await _accessGranted(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The rule is in place either way; the shortcut picks it up on the next start.
            Trace.WriteLine($"[KeyboardAccessSetupTask] Restarting the shortcut failed ({ex.GetType().Name}).");
        }

        return new SetupActionOutcome(true, _localizer["Setup.KeyboardAccess.Granted"]);
    }
}
