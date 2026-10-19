using System.Runtime.Versioning;
using Juvin.Core.Localization;
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

    private AccessibilityPermissionSetupTask CreateTask(bool listening = false) =>
        new(new Localizer(), _runner, _trust, _ => Task.CompletedTask, () => listening);

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
            () => listening
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
            () => false
        );
        _trust.Trusted = true;

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Failed, state.Kind);
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
        var task = new AccessibilityPermissionSetupTask(new Localizer(), new FakeProcessRunner(exitCode: 1), _trust, _ => Task.CompletedTask, () => false);

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
            () => false
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
            () => listening
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
}
