using System.Runtime.Versioning;
using Juvin.Core.Localization;
using Juvin.Platform.Mac.Launcher;
using Juvin.Platform.Mac.Setup;
using Juvin.Platform.Setup;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

// The native trust calls sit behind a fake, so the state machine runs on every OS.
[SupportedOSPlatform("macos")]
public sealed class MacAccessibilityPermissionSetupTaskTests
{
    private readonly FakeTrust _trust = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeRelauncher _relauncher = new();

    private AccessibilityPermissionSetupTask CreateTask(bool listening = false, bool needsPermission = true) =>
        new(new Localizer(), _runner, _trust, _ => Task.CompletedTask, () => listening, _relauncher, () => needsPermission);

    [Fact]
    public async Task Trusted_process_with_a_listening_hook_is_satisfied()
    {
        _trust.Trusted = true;

        var state = await CreateTask(listening: true).EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Satisfied, state.Kind);
        Assert.Null(state.ActionLabel);
    }

    [Fact]
    public async Task Trusted_process_whose_hook_does_not_start_reports_the_failure()
    {
        _trust.Trusted = true;

        var state = await CreateTask().EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Failed, state.Kind);
        Assert.Equal(new Localizer()["Setup.Accessibility.NotListening"], state.Summary);
    }

    [Fact]
    public async Task Evaluate_KeepsRetryingTheRestartWhileTrustedAndNotListening_ThenReportsSatisfied()
    {
        var restarts = 0;
        var listening = false;
        var task = new AccessibilityPermissionSetupTask(
            new Localizer(),
            _runner,
            _trust,
            _ =>
            {
                restarts++;
                listening = restarts == 3;
                return Task.CompletedTask;
            },
            // ReSharper disable once AccessToModifiedClosure -- the fake restart flips the flag on purpose
            () => listening,
            _relauncher
        );
        _trust.Trusted = true;

        Assert.Equal(SetupTaskStatusKind.Failed, (await task.EvaluateAsync(CancellationToken.None)).Kind);
        Assert.Equal(SetupTaskStatusKind.Failed, (await task.EvaluateAsync(CancellationToken.None)).Kind);
        Assert.Equal(SetupTaskStatusKind.Satisfied, (await task.EvaluateAsync(CancellationToken.None)).Kind);
        Assert.Equal(SetupTaskStatusKind.Satisfied, (await task.EvaluateAsync(CancellationToken.None)).Kind);
        Assert.Equal(3, restarts);
    }

    [Fact]
    public async Task Evaluate_ReportsAFailedRestartThatThrows()
    {
        var task = new AccessibilityPermissionSetupTask(
            new Localizer(),
            _runner,
            _trust,
            _ => throw new InvalidOperationException("no hook"),
            () => false,
            _relauncher
        );
        _trust.Trusted = true;

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Failed, state.Kind);
    }

    [Fact]
    public async Task Trusted_process_whose_hook_does_not_start_offers_a_restart_when_it_can_relaunch()
    {
        _trust.Trusted = true;
        _relauncher.CanRelaunch = true;

        var state = await CreateTask().EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.NeedsAction, state.Kind);
        Assert.Equal(new Localizer()["Setup.Accessibility.NotListening"], state.Summary);
        Assert.Equal(new Localizer()["Setup.Accessibility.RestartDetail"], state.Detail);
        Assert.Equal("Restart Juvin", state.ActionLabel);
    }

    [Fact]
    public async Task A_hook_that_failed_for_another_reason_is_not_offered_a_restart()
    {
        _trust.Trusted = true;
        _relauncher.CanRelaunch = true;
        var task = CreateTask(needsPermission: false);

        var state = await task.EvaluateAsync(CancellationToken.None);
        await task.RunActionAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Failed, state.Kind);
        Assert.Equal(new Localizer()["Setup.Accessibility.OtherFailureDetail"], state.Detail);
        Assert.Null(state.ActionLabel);
        Assert.Equal(0, _relauncher.Relaunches);
    }

    [Fact]
    public async Task Action_relaunches_instead_of_opening_settings_while_trusted_and_not_listening()
    {
        _trust.Trusted = true;
        _relauncher.CanRelaunch = true;

        var outcome = await CreateTask().RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, _relauncher.Relaunches);
        Assert.Equal(0, _trust.Prompts);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Action_reports_a_relaunch_that_could_not_start()
    {
        _trust.Trusted = true;
        _relauncher.CanRelaunch = true;
        _relauncher.Succeeds = false;

        var outcome = await CreateTask().RunActionAsync(CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(new Localizer()["Setup.Accessibility.RestartFailed"], outcome.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Action_opens_settings_without_relaunching_when_untrusted_or_listening(bool trusted, bool listening)
    {
        _trust.Trusted = trusted;
        _relauncher.CanRelaunch = true;

        var outcome = await CreateTask(listening).RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(0, _relauncher.Relaunches);
        Assert.Single(_runner.Calls);
    }

    [Fact]
    public async Task Untrusted_process_needs_action()
    {
        var state = await CreateTask().EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.NeedsAction, state.Kind);
        Assert.Equal("Open Accessibility settings", state.ActionLabel);
        Assert.Null(state.CopyCommand);
    }

    [Fact]
    public async Task Action_prompts_and_opens_accessibility_settings()
    {
        var outcome = await CreateTask().RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, _trust.Prompts);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal("open", call.FileName);
        Assert.Equal([AccessibilityPermissionSetupTask.SettingsUrl], call.Arguments);
    }

    [Fact]
    public async Task Failed_open_reports_failure()
    {
        var task = new AccessibilityPermissionSetupTask(new Localizer(), new FakeProcessRunner(exitCode: 1), _trust, _ => Task.CompletedTask, () => false, _relauncher);

        var outcome = await task.RunActionAsync(CancellationToken.None);

        Assert.False(outcome.Success);
    }

    [Fact]
    public void Applies_only_on_macos()
    {
        var task = CreateTask();

        Assert.Equal(OperatingSystem.IsMacOS(), task.AppliesToThisMachine());
        Assert.Equal("accessibility", task.Id);
        Assert.Equal(SetupTaskSeverity.Recommended, task.Severity);
    }

    [Fact]
    public async Task Evaluate_RestartsTheHotkeyOnEveryLookWhileTrustedAndNotListening()
    {
        var restarts = 0;
        var task = new AccessibilityPermissionSetupTask(
            new Localizer(),
            _runner,
            _trust,
            _ =>
            {
                restarts++;
                return Task.CompletedTask;
            },
            () => false,
            _relauncher
        );

        _trust.Trusted = true;
        await task.EvaluateAsync(CancellationToken.None);
        await task.EvaluateAsync(CancellationToken.None);
        _trust.Trusted = false;
        await task.EvaluateAsync(CancellationToken.None);
        _trust.Trusted = true;
        await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(3, restarts);
    }

    [Fact]
    public async Task Evaluate_DoesNotRestartTheHotkeyWhenItAlreadyListens()
    {
        var restarts = 0;
        var listening = true;
        var task = new AccessibilityPermissionSetupTask(
            new Localizer(),
            _runner,
            _trust,
            _ =>
            {
                restarts++;
                return Task.CompletedTask;
            },
            // ReSharper disable once AccessToModifiedClosure -- the test flips the flag between evaluations on purpose
            () => listening,
            _relauncher
        );

        _trust.Trusted = true;
        await task.EvaluateAsync(CancellationToken.None);
        Assert.Equal(0, restarts);

        _trust.Trusted = false;
        await task.EvaluateAsync(CancellationToken.None);
        listening = false;
        _trust.Trusted = true;
        await task.EvaluateAsync(CancellationToken.None);
        Assert.Equal(1, restarts);
    }

    private sealed class FakeTrust : IAccessibilityTrust
    {
        public bool Trusted { get; set; }
        public int Prompts { get; private set; }

        public bool IsTrusted() => Trusted;

        public void Prompt() => Prompts++;
    }

    private sealed class FakeRelauncher : IAppRelauncher
    {
        public bool CanRelaunch { get; set; }
        public bool Succeeds { get; set; } = true;
        public int Relaunches { get; private set; }

        public bool Relaunch()
        {
            Relaunches++;
            return Succeeds;
        }
    }
}
