using System.Runtime.Versioning;
using System.ComponentModel;
using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Platform.Linux.Setup;
using Juvin.Platform.Setup;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class KeyboardAccessSetupTaskTests
{
    private readonly Dictionary<string, string> _environment = new() { ["XDG_SESSION_TYPE"] = "wayland" };
    private bool _hasAccess;
    private bool? _inInputGroup = false;
    private int _restarts;

    private KeyboardAccessSetupTask Create(IProcessRunner? runner = null) =>
        new(
            new Localizer(),
            runner ?? new FakeProcessRunner(),
            _ =>
            {
                _restarts++;
                return Task.CompletedTask;
            },
            () => _hasAccess,
            () => _inInputGroup,
            name => _environment.GetValueOrDefault(name)
        );

    [LinuxFact]
    public void AppliesOnlyToWaylandSessions()
    {
        Assert.True(Create().AppliesToThisMachine());

        _environment["XDG_SESSION_TYPE"] = "x11";
        Assert.False(Create().AppliesToThisMachine());

        _environment["WAYLAND_DISPLAY"] = "wayland-0";
        Assert.True(Create().AppliesToThisMachine());
    }

    [LinuxFact]
    public void IsARecommendedTask()
    {
        var task = Create();

        Assert.Equal("keyboard-access", task.Id);
        Assert.Equal(SetupTaskSeverity.Recommended, task.Severity);
        Assert.Equal("Keyboard access", task.Title);
    }

    [LinuxFact]
    public async Task Evaluate_IsSatisfiedWhenAKeyboardOpens()
    {
        _hasAccess = true;

        var state = await Create().EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Satisfied, state.Kind);
        Assert.Null(state.ActionLabel);
    }

    [LinuxFact]
    public async Task Evaluate_OffersTheRuleAndTheDesktopShortcut()
    {
        var state = await Create().EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.NeedsAction, state.Kind);
        Assert.Equal("Allow keyboard access", state.ActionLabel);
        Assert.Contains("desktop shortcut", state.Detail);
        Assert.DoesNotContain("input group", state.Detail);
        Assert.Equal(KeyboardAccessSetupTask.ManualCommand, state.CopyCommand);
        Assert.Contains(KeyboardAccessSetupTask.RulePath, state.CopyCommand);
    }

    [LinuxFact]
    public async Task Evaluate_SuggestsSigningOutToAnInputGroupMember()
    {
        _inInputGroup = true;

        var state = await Create().EvaluateAsync(CancellationToken.None);

        Assert.Contains("input group", state.Detail);
    }

    [LinuxFact]
    public async Task RunAction_InstallsTheRuleThroughPkexec_AndRestartsTheShortcut()
    {
        var runner = new FakeProcessRunner();

        var outcome = await Create(runner).RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, _restarts);
        var (fileName, arguments) = Assert.Single(runner.Calls);
        Assert.Equal("pkexec", fileName);
        Assert.Equal("/bin/sh", arguments[0]);
        Assert.Contains("udevadm control --reload-rules", arguments[2]);
        Assert.EndsWith("|| exit 1", arguments[2]);
        Assert.Equal(
            [
                "# Juvin: lets the signed-in user read keyboards for its global shortcut.",
                "SUBSYSTEM==\"input\", ENV{ID_INPUT_KEYBOARD}==\"1\", TAG+=\"uaccess\", GROUP=\"input\", MODE=\"0660\"",
                "/etc/udev/rules.d/70-juvin-input.rules",
            ],
            arguments.Skip(4)
        );
    }

    [Theory]
    [InlineData(126, "closed")]
    [InlineData(127, "no password prompt")]
    [InlineData(1, "exit code 1")]
    public async Task RunAction_ReportsAFailureWithoutRestarting(int exitCode, string expected)
    {
        var outcome = await Create(new FakeProcessRunner(exitCode)).RunActionAsync(CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains(expected, outcome.Message);
        Assert.Equal(0, _restarts);
    }

    [LinuxFact]
    public async Task RunAction_WithoutPkexec_PointsAtTheManualCommand()
    {
        var outcome = await Create(new ThrowingRunner()).RunActionAsync(CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("pkexec", outcome.Message);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput, string? workingDirectory, CancellationToken ct) =>
            throw new Win32Exception(2);
    }
}
