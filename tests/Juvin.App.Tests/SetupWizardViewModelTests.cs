using System.Text.Json;
using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels;
using Juvin.Core.Connector;
using Juvin.Core.Hotkeys;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;
using Juvin.Platform;
using Juvin.Platform.Setup;

namespace Juvin.App.Tests;

public sealed class SetupWizardViewModelTests : IAsyncDisposable
{
    private const string Hello =
        """{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"chrome","version":"140.0"}}""";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly ConnectorService _connector;
    private readonly FakeCrashReporter _crashReporter = new();
    private readonly Localizer _localizer = new();
    private readonly FakeConnectorChannel _menuChannel = new();
    private readonly Task _menuLoop;
    private readonly ActionMenuService _menus;
    private readonly ConnectorSession _menuSession;
    private readonly RewritePanelViewModel _panel;
    private readonly FakeProviders _providers = new();
    private readonly FakeNativeHostRegistrar _registrar = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeConnectorSocket _socket = new();

    public SetupWizardViewModelTests()
    {
        _connector = new ConnectorService(_socket, _registrar, _settings, _localizer);
        var model = new FakeModelProvider(ProcessingMode.Local);
        var providers = new ProviderSelector(_ => model, _ => model);
        var history = new FakeHistoryService();
        _panel = new RewritePanelViewModel(providers, _settings, history, _localizer, action => action());
        var panelService = new RewritePanelService(_panel, () => { }, () => { }, action => action());
        _menus = new ActionMenuService(_connector, panelService, providers, _settings, history, _localizer, action => action());
        _menuSession = new ConnectorSession(_menuChannel, () => _connector.BuildConfig(_settings.Current));
        _menuSession.MenuChosen += _menus.OnMenuChosen;
        _menuLoop = _menuSession.RunAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _menuSession.DisposeAsync();
        await _menuLoop.WaitAsync(s_timeout);
        await _connector.StopAsync();
    }

    [Fact]
    public async Task UnsatisfiedRequiredTask_BlocksFinish()
    {
        var vm = await CreateOnLastStepAsync(NewTask("needs-fix", SetupTaskSeverity.Required, SetupTaskStatusKind.NeedsAction));
        var closed = false;
        vm.RequestClose += (_, _) => closed = true;

        vm.NextCommand.Execute(null);

        Assert.False(vm.CanAdvance);
        Assert.False(closed);
        Assert.False(_settings.Current.HasCompletedOnboarding);
    }

    [Fact]
    public async Task SatisfiedRequiredTask_AllowsFinish()
    {
        var vm = await CreateOnLastStepAsync(
            NewTask("ready", SetupTaskSeverity.Required, SetupTaskStatusKind.Satisfied),
            NewTask("optional", SetupTaskSeverity.Recommended, SetupTaskStatusKind.NeedsAction)
        );
        var closed = false;
        vm.RequestClose += (_, _) => closed = true;

        vm.NextCommand.Execute(null);

        Assert.True(vm.CanAdvance);
        Assert.True(closed);
        Assert.True(_settings.Current.HasCompletedOnboarding);
    }

    [Fact]
    public async Task Skip_BypassesUnsatisfiedRequiredTasks()
    {
        var vm = await CreateOnLastStepAsync(NewTask("needs-fix", SetupTaskSeverity.Required, SetupTaskStatusKind.Failed));
        var closed = false;
        vm.RequestClose += (_, _) => closed = true;

        vm.SkipCommand.Execute(null);

        Assert.True(closed);
        Assert.True(_settings.Current.HasCompletedOnboarding);
    }

    [Fact]
    public async Task FailedSave_KeepsTheWizardOpenAndShowsTheError()
    {
        var vm = await CreateOnLastStepAsync();
        var closed = false;
        vm.RequestClose += (_, _) => closed = true;
        _settings.UpdateFailure = new IOException("disk full");

        vm.SkipCommand.Execute(null);

        Assert.False(closed);
        Assert.Contains("disk full", vm.SaveError);
        Assert.False(_settings.Current.HasCompletedOnboarding);
    }

    [Fact]
    public async Task TheChecklistStep_RechecksOpenRows_AndClearsAFailureOnceFixed()
    {
        var task = new FakeSetupTask("keyboard", SetupTaskSeverity.Required, SetupTaskStatusKind.NeedsAction)
        {
            ActionOutcome = new SetupActionOutcome(false, "Run the command below."),
        };
        using var vm = Create(task);
        vm.SetupRecheckInterval = TimeSpan.FromMilliseconds(20);
        vm.StepIndex = (int)SetupWizardStep.Checklist;
        await WaitUntilAsync(() => vm.SetupItems[0].Kind == SetupTaskStatusKind.NeedsAction);
        await vm.RunSetupActionCommand.ExecuteAsync(vm.SetupItems[0]);
        Assert.Equal("Run the command below.", vm.SetupItems[0].ActionMessage);

        task.State = new SetupTaskState(SetupTaskStatusKind.Satisfied, "fixed in a terminal");
        await WaitUntilAsync(() => vm.SetupItems[0].IsSatisfied);

        Assert.Null(vm.SetupItems[0].ActionMessage);
        Assert.True(vm.CanAdvance);
    }

    [Fact]
    public async Task TheChecklistRecheck_KeepsGoing_WhenAnEvaluationIsCanceledOnItsOwn()
    {
        var task = new FakeSetupTask("flaky", SetupTaskSeverity.Required, SetupTaskStatusKind.NeedsAction)
        {
            Evaluation = n => n switch
            {
                1 => Task.FromResult(new SetupTaskState(SetupTaskStatusKind.NeedsAction, "open")),
                2 => Task.FromCanceled<SetupTaskState>(new CancellationToken(true)),
                _ => Task.FromResult(new SetupTaskState(SetupTaskStatusKind.Satisfied, "fixed")),
            },
        };
        using var vm = Create(task);
        vm.SetupRecheckInterval = TimeSpan.FromMilliseconds(20);
        vm.StepIndex = (int)SetupWizardStep.Checklist;

        await WaitUntilAsync(() => vm.SetupItems[0].IsSatisfied);
    }

    [Fact]
    public async Task TheChecklistRecheck_SkipsSatisfiedRows_AndStopsOffTheStep()
    {
        var open = NewTask("open", SetupTaskSeverity.Recommended, SetupTaskStatusKind.NeedsAction);
        var done = NewTask("done", SetupTaskSeverity.Required, SetupTaskStatusKind.Satisfied);
        using var vm = Create(open, done);
        vm.SetupRecheckInterval = TimeSpan.FromMilliseconds(20);
        vm.StepIndex = (int)SetupWizardStep.Checklist;
        await WaitUntilAsync(() => open.EvaluateCount > 2);
        Assert.Equal(1, done.EvaluateCount);

        vm.StepIndex = (int)SetupWizardStep.TryIt;
        var evaluations = open.EvaluateCount;
        await Task.Delay(200);
        Assert.Equal(evaluations, open.EvaluateCount);
    }

    [Theory]
    [InlineData(SetupTaskStatusKind.NeedsAction, "Restart")]
    [InlineData(SetupTaskStatusKind.Satisfied, null)]
    public async Task ALaterChangeAfterASuccessfulAction_ClearsItsMessage(SetupTaskStatusKind later, string? laterAction)
    {
        var task = new FakeSetupTask("keyboard", SetupTaskSeverity.Recommended, SetupTaskStatusKind.NeedsAction)
        {
            ActionOutcome = new SetupActionOutcome(true, "Turn it on, then check again."),
        };
        using var vm = Create(task);
        vm.SetupRecheckInterval = TimeSpan.FromMilliseconds(20);
        vm.StepIndex = (int)SetupWizardStep.Checklist;
        await WaitUntilAsync(() => vm.SetupItems[0].Kind == SetupTaskStatusKind.NeedsAction);
        await vm.RunSetupActionCommand.ExecuteAsync(vm.SetupItems[0]);
        Assert.Equal("Turn it on, then check again.", vm.SetupItems[0].ActionMessage);

        task.State = new SetupTaskState(later, "changed", ActionLabel: laterAction);
        await WaitUntilAsync(() => vm.SetupItems[0].Summary == "changed");

        Assert.Null(vm.SetupItems[0].ActionMessage);
    }

    [Fact]
    public async Task AnActionWhoseResultOffersAnotherAction_DropsItsMessage()
    {
        var task = new FakeSetupTask("keyboard", SetupTaskSeverity.Recommended, SetupTaskStatusKind.NeedsAction)
        {
            ActionOutcome = new SetupActionOutcome(true, "Turn it on, then check again."),
            StateAfterAction = new SetupTaskState(SetupTaskStatusKind.NeedsAction, "restart", ActionLabel: "Restart"),
        };
        var vm = await CreateOnLastStepAsync(task);

        await vm.RunSetupActionCommand.ExecuteAsync(vm.SetupItems[0]);

        Assert.Equal("Restart", vm.SetupItems[0].ActionLabel);
        Assert.Null(vm.SetupItems[0].ActionMessage);
    }

    [Fact]
    public async Task ARecheckThatChangesNothing_KeepsTheActionsMessage()
    {
        var task = new FakeSetupTask("keyboard", SetupTaskSeverity.Recommended, SetupTaskStatusKind.NeedsAction)
        {
            ActionOutcome = new SetupActionOutcome(true, "Turn it on, then check again."),
        };
        var vm = await CreateOnLastStepAsync(task);
        await vm.RunSetupActionCommand.ExecuteAsync(vm.SetupItems[0]);

        await vm.RecheckSetupCommand.ExecuteAsync(null);

        Assert.Equal("Turn it on, then check again.", vm.SetupItems[0].ActionMessage);
    }

    [Fact]
    public void AnUnfinishedSetup_ReopensAtTheSavedStep_AndSavesTheStepItClosesAt()
    {
        _settings.Update(s => s with { SetupWizardStep = nameof(SetupWizardStep.Checklist) });
        using var vm = Create();
        Assert.Equal(SetupWizardStep.Checklist, vm.Step);

        vm.StepIndex = (int)SetupWizardStep.TryIt;
        // ReSharper disable once DisposeOnUsingVariable -- disposing is what the test checks; the using covers a failure before it
        vm.Dispose();

        Assert.Equal(nameof(SetupWizardStep.TryIt), _settings.Current.SetupWizardStep);
    }

    [Fact]
    public void AnEarlierWizardDisposedAgain_KeepsTheLaterWizardsStep()
    {
        using var first = Create();
        first.StepIndex = (int)SetupWizardStep.Shortcut;
        // ReSharper disable once DisposeOnUsingVariable -- disposing is what the test checks; the using covers a failure before it
        first.Dispose();
        using var second = Create();
        second.StepIndex = (int)SetupWizardStep.Checklist;
        // ReSharper disable once DisposeOnUsingVariable -- disposing is what the test checks; the using covers a failure before it
        second.Dispose();

        // ReSharper disable once DisposeOnUsingVariable -- disposing is what the test checks; the using covers a failure before it
        first.Dispose();

        Assert.Equal(nameof(SetupWizardStep.Checklist), _settings.Current.SetupWizardStep);
    }

    [Fact]
    public void AFinishedSetup_OpensAtTheFirstStep_AndSavesNoStep()
    {
        _settings.Update(s => s with { HasCompletedOnboarding = true, SetupWizardStep = nameof(SetupWizardStep.Checklist) });
        using var vm = Create();
        Assert.Equal(0, vm.StepIndex);

        vm.StepIndex = (int)SetupWizardStep.TryIt;
        // ReSharper disable once DisposeOnUsingVariable -- disposing is what the test checks; the using covers a failure before it
        vm.Dispose();

        Assert.Equal(nameof(SetupWizardStep.Checklist), _settings.Current.SetupWizardStep);
    }

    [Fact]
    public async Task RunningAnAction_ReevaluatesEveryRow()
    {
        var fixable = new FakeSetupTask("fixable", SetupTaskSeverity.Required, SetupTaskStatusKind.NeedsAction)
        {
            StateAfterAction = new SetupTaskState(SetupTaskStatusKind.Satisfied, "fixed"),
        };
        var other = NewTask("other", SetupTaskSeverity.Required, SetupTaskStatusKind.Satisfied);
        var vm = await CreateOnLastStepAsync(fixable, other);
        var otherEvaluations = other.EvaluateCount;

        await vm.RunSetupActionCommand.ExecuteAsync(vm.SetupItems[0]);

        Assert.Equal(1, fixable.ActionCount);
        Assert.True(other.EvaluateCount > otherEvaluations);
        Assert.True(vm.SetupItems[0].IsSatisfied);
        Assert.Equal("Done", vm.SetupItems[0].ActionMessage);
        Assert.True(vm.CanAdvance);
    }

    [Fact]
    public async Task ASecondRowsAction_CanStartWhileTheFirstRuns()
    {
        var firstGate = new TaskCompletionSource();
        var secondGate = new TaskCompletionSource();
        var first = new FakeSetupTask("first", SetupTaskSeverity.Required, SetupTaskStatusKind.NeedsAction) { ActionGate = firstGate.Task };
        var second = new FakeSetupTask("second", SetupTaskSeverity.Required, SetupTaskStatusKind.NeedsAction) { ActionGate = secondGate.Task };
        var vm = await CreateOnLastStepAsync(first, second);

        var firstRun = vm.RunSetupActionCommand.ExecuteAsync(vm.SetupItems[0]);
        await WaitUntilAsync(() => first.ActionCount == 1);

        Assert.True(vm.SetupItems[0].IsBusy);
        Assert.False(vm.SetupItems[0].CanRunAction);
        Assert.True(vm.RunSetupActionCommand.CanExecute(vm.SetupItems[1]));
        var secondRun = vm.RunSetupActionCommand.ExecuteAsync(vm.SetupItems[1]);
        await WaitUntilAsync(() => second.ActionCount == 1);
        Assert.True(vm.SetupItems[1].IsBusy);

        firstGate.SetResult();
        secondGate.SetResult();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(s_timeout);

        Assert.False(vm.SetupItems[0].IsBusy);
        Assert.False(vm.SetupItems[1].IsBusy);
    }

    [Fact]
    public async Task AnOlderCheckFinishingLast_DoesNotOverrideTheNewerResult()
    {
        var older = new TaskCompletionSource<SetupTaskState>();
        var newer = new TaskCompletionSource<SetupTaskState>();
        var task = new FakeSetupTask("server", SetupTaskSeverity.Required, SetupTaskStatusKind.NeedsAction)
        {
            Evaluation = n => n == 1 ? older.Task : newer.Task,
        };
        var vm = Create(task);

        var olderCheck = vm.RecheckSetupCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => task.EvaluateCount == 1);
        vm.StepIndex = (int)SetupWizardStep.Done;
        await WaitUntilAsync(() => task.EvaluateCount == 2);

        Assert.False(vm.AllRequiredReady);
        newer.SetResult(new SetupTaskState(SetupTaskStatusKind.Failed, "down"));
        await WaitUntilAsync(() => vm.SetupItems[0].Kind == SetupTaskStatusKind.Failed);
        older.SetResult(new SetupTaskState(SetupTaskStatusKind.Satisfied, "up"));
        await olderCheck.WaitAsync(s_timeout);

        Assert.Equal(SetupTaskStatusKind.Failed, vm.SetupItems[0].Kind);
        Assert.False(vm.CanAdvance);
    }

    [Fact]
    public async Task APendingCheck_KeepsFinishDisabled()
    {
        var pending = new TaskCompletionSource<SetupTaskState>();
        var task = new FakeSetupTask("server", SetupTaskSeverity.Required, SetupTaskStatusKind.Satisfied)
        {
            Evaluation = n => n == 1 ? Task.FromResult(new SetupTaskState(SetupTaskStatusKind.Satisfied, "up")) : pending.Task,
        };
        var vm = Create(task);
        await vm.RecheckSetupCommand.ExecuteAsync(null);
        Assert.True(vm.AllRequiredReady);

        vm.StepIndex = (int)SetupWizardStep.Done;

        Assert.True(vm.SetupItems[0].IsSatisfied);
        Assert.False(vm.CanAdvance);
        pending.SetResult(new SetupTaskState(SetupTaskStatusKind.Satisfied, "up"));
        await WaitUntilAsync(() => vm.CanAdvance);
    }

    [Fact]
    public void TasksThatDoNotApply_AreLeftOut()
    {
        var vm = Create(
            NewTask("applies", SetupTaskSeverity.Required, SetupTaskStatusKind.Satisfied),
            new FakeSetupTask("elsewhere", SetupTaskSeverity.Required, SetupTaskStatusKind.NeedsAction) { Applies = false }
        );

        Assert.Equal(["applies"], vm.SetupItems.Select(row => row.Title));
    }

    [Fact]
    public void EarlierSteps_AdvanceRegardlessOfChecklist()
    {
        var vm = Create(NewTask("needs-fix", SetupTaskSeverity.Required, SetupTaskStatusKind.NeedsAction));

        Assert.True(vm.CanAdvance);
        vm.NextCommand.Execute(null);

        Assert.Equal(SetupWizardStep.ProcessingMode, vm.Step);
        Assert.True(vm.CanAdvance);
        vm.NextCommand.Execute(null);

        Assert.Equal(SetupWizardStep.BrowserConnector, vm.Step);
        Assert.True(vm.CanAdvance);
        vm.NextCommand.Execute(null);

        Assert.Equal(SetupWizardStep.Shortcut, vm.Step);
        Assert.True(vm.CanAdvance);
        vm.NextCommand.Execute(null);

        Assert.Equal(SetupWizardStep.Checklist, vm.Step);
        Assert.False(_settings.Current.HasCompletedOnboarding);
    }

    [Fact]
    public void Steps_RunWelcomeModeBrowserShortcutChecklistTryItDone()
    {
        Assert.Equal(7, SetupWizardViewModel.StepCount);
        Assert.Equal(1, (int)SetupWizardStep.ProcessingMode);
        Assert.Equal(SetupWizardStep.BrowserConnector, SetupWizardStep.ProcessingMode + 1);
        Assert.Equal(SetupWizardStep.Shortcut, SetupWizardStep.BrowserConnector + 1);
        Assert.Equal(SetupWizardStep.Checklist, SetupWizardStep.Shortcut + 1);
        Assert.Equal(SetupWizardStep.TryIt, SetupWizardStep.Checklist + 1);
        Assert.Equal(SetupWizardStep.Done, SetupWizardStep.TryIt + 1);
    }

    [Fact]
    public async Task OpenSample_WithNoBrowser_SaysSo()
    {
        var vm = Create();
        vm.StepIndex = (int)SetupWizardStep.TryIt;

        await vm.OpenSampleCommand.ExecuteAsync(null);

        Assert.False(vm.IsBrowserConnected);
        Assert.False(vm.CanOpenSample);
        Assert.Equal(_localizer["Wizard.TryIt.NoBrowser"], vm.TryItStatus);
        Assert.Equal(TryItPhase.NotStarted, vm.TryItPhase);
        Assert.True(vm.CanAdvance);
    }

    [Fact]
    public async Task TryIt_TicksTheRowsAsTheSampleOpensTheDraftIsCapturedAndTheRewriteShows()
    {
        var vm = Create();
        await _connector.StartAsync(CancellationToken.None);
        await using var stream = await ConnectAndSayHelloAsync();
        await WaitUntilAsync(() => vm.IsBrowserConnected);

        var open = vm.OpenSampleCommand.ExecuteAsync(null);
        var request = await ReadAsync(stream);
        Assert.Equal("testpage.request", request.GetProperty("type").GetString());
        await WriteAsync(stream, $$"""{"v":1,"type":"testpage.response","id":"{{request.GetProperty("id").GetString()}}","ok":true}""");
        await open;

        Assert.True(vm.IsSampleOpened);
        Assert.Equal(TryItPhase.SampleOpened, vm.TryItPhase);
        Assert.Equal(_localizer["Wizard.TryIt.Waiting"], vm.TryItStatus);

        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        var id = (await ReadAsync(stream)).GetProperty("id").GetString()!;
        await WriteAsync(stream, CaptureResponse(id));
        await WaitUntilAsync(() => vm.IsCaptured);

        Assert.False(vm.IsRewriteShown);
        await LoadAndRunInPanelAsync();

        Assert.True(vm.IsRewriteShown);
        Assert.Equal(TryItPhase.RewriteShown, vm.TryItPhase);
    }

    [Fact]
    public async Task TryIt_TicksTheRowsWhenTheMenuShowsAndItsRewriteIsApplied()
    {
        var vm = Create();

        var menuId = await ShowMenuAsync();

        Assert.True(vm.IsCaptured);
        Assert.False(vm.IsRewriteShown);
        Assert.Null(vm.TryItStatus);

        _menuChannel.Deliver($$"""{"v":1,"type":"menu.choose","menuId":"{{menuId}}","action":"soften"}""");
        await NextMenuMessageAsync("menu.update");
        var apply = await NextMenuMessageAsync("apply.request");
        Assert.False(vm.IsRewriteShown);
        _menuChannel.Deliver($$"""{"v":1,"type":"apply.response","id":"{{apply.GetProperty("id").GetString()}}","ok":true}""");

        await WaitUntilAsync(() => vm.IsRewriteShown);
        Assert.Equal(TryItPhase.RewriteShown, vm.TryItPhase);
    }

    [Fact]
    public async Task AMenuShownAfterADeclinedCapture_TicksTheCapturedRowAndClearsTheReason()
    {
        var vm = Create();
        await _connector.StartAsync(CancellationToken.None);
        await using var stream = await ConnectAndSayHelloAsync();
        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        var id = (await ReadAsync(stream)).GetProperty("id").GetString();
        await WriteAsync(
            stream,
            $$$"""{"v":1,"type":"context.response","id":"{{{id}}}","ok":false,"error":{"code":"no-selection","message":"Declined."}}"""
        );
        await WaitUntilAsync(() => vm.TryItPhase == TryItPhase.CaptureFailed);

        await ShowMenuAsync();

        Assert.Equal(TryItPhase.Captured, vm.TryItPhase);
        Assert.Null(vm.TryItStatus);
    }

    [Fact]
    public async Task AfterDispose_AMenuShownChangesNothing()
    {
        var vm = Create();
        vm.Dispose();

        await ShowMenuAsync();

        Assert.Equal(TryItPhase.NotStarted, vm.TryItPhase);
    }

    [Fact]
    public async Task ADeclinedCapture_ShowsTheLocalizedReason()
    {
        var vm = Create();
        await _connector.StartAsync(CancellationToken.None);
        await using var stream = await ConnectAndSayHelloAsync();

        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        var id = (await ReadAsync(stream)).GetProperty("id").GetString();
        await WriteAsync(
            stream,
            $$$"""{"v":1,"type":"context.response","id":"{{{id}}}","ok":false,"error":{"code":"no-selection","message":"Declined."}}"""
        );
        // The reason is set just after the phase.
        await WaitUntilAsync(() => vm is { TryItPhase: TryItPhase.CaptureFailed, HasTryItStatus: true });

        Assert.Equal(_localizer.Format("Wizard.TryIt.CaptureFailed", _localizer["Panel.Error.NoSelection"]), vm.TryItStatus);
        Assert.False(vm.IsCaptured);
    }

    [Fact]
    public async Task ARecaptureFromThePanel_ClearsTheDeclinedCaptureAndTicksTheRows()
    {
        var vm = Create();
        await _connector.StartAsync(CancellationToken.None);
        await using var stream = await ConnectAndSayHelloAsync();

        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        var id = (await ReadAsync(stream)).GetProperty("id").GetString();
        await WriteAsync(
            stream,
            $$$"""{"v":1,"type":"context.response","id":"{{{id}}}","ok":false,"error":{"code":"no-selection","message":"Declined."}}"""
        );
        await WaitUntilAsync(() => vm.TryItPhase == TryItPhase.CaptureFailed);

        // The panel's Recapture talks to the session directly, so only the panel's state reports it.
        await LoadAndRunInPanelAsync();

        Assert.True(vm.IsCaptured);
        Assert.True(vm.IsRewriteShown);
        Assert.Null(vm.TryItStatus);
    }

    [Fact]
    public async Task ALaterDeclinedCapture_KeepsTheTickedRows()
    {
        var vm = Create();
        await _connector.StartAsync(CancellationToken.None);
        await using var stream = await ConnectAndSayHelloAsync();
        await LoadAndRunInPanelAsync();
        Assert.True(vm.IsRewriteShown);

        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        var id = (await ReadAsync(stream)).GetProperty("id").GetString();
        await WriteAsync(
            stream,
            $$$"""{"v":1,"type":"context.response","id":"{{{id}}}","ok":false,"error":{"code":"no-selection","message":"Declined."}}"""
        );
        await WaitUntilAsync(() => vm.HasTryItStatus);

        Assert.True(vm.IsCaptured);
        Assert.True(vm.IsRewriteShown);
        Assert.Equal(_localizer.Format("Wizard.TryIt.CaptureFailed", _localizer["Panel.Error.NoSelection"]), vm.TryItStatus);
    }

    [Fact]
    public async Task AFailedOpenAfterAnEarlierCapture_StillReportsTheFailure()
    {
        var vm = Create();
        await _connector.StartAsync(CancellationToken.None);
        await using var stream = await ConnectAndSayHelloAsync();
        await WaitUntilAsync(() => vm.IsBrowserConnected);
        await LoadAndRunInPanelAsync();

        var open = vm.OpenSampleCommand.ExecuteAsync(null);
        var request = await ReadAsync(stream);
        await WriteAsync(
            stream,
            $$$"""{"v":1,"type":"testpage.response","id":"{{{request.GetProperty("id").GetString()}}}","ok":false,"error":{"code":"internal","message":"Failed."}}"""
        );
        await open;

        Assert.True(vm.IsRewriteShown);
        Assert.Equal(_localizer.Format("Wizard.TryIt.OpenFailed", _localizer["Panel.Error.Unknown"]), vm.TryItStatus);
    }

    [Fact]
    public async Task AfterDispose_CapturesAndRewritesChangeNothing()
    {
        var vm = Create();
        vm.Dispose();

        await _connector.TriggerCaptureAsync();
        await LoadAndRunInPanelAsync();

        Assert.Equal(TryItPhase.NotStarted, vm.TryItPhase);
        Assert.Null(vm.TryItStatus);
    }

    [Fact]
    public void EnteringTheBrowserStep_DetectsBrowsers()
    {
        _registrar.Browsers.Add(new BrowserTarget("chrome", "Google Chrome", BrowserFamily.Chromium, BrowserPackaging.Native));
        var vm = Create();
        var detections = _registrar.DetectCount;

        vm.StepIndex = (int)SetupWizardStep.BrowserConnector;

        Assert.Equal(detections + 1, _registrar.DetectCount);
        Assert.Equal("Google Chrome", Assert.Single(vm.Connector.Browsers).DisplayName);
        Assert.True(vm.CanAdvance);
    }

    [Fact]
    public async Task ModeStep_NeverBlocksNext_EvenWithNothingDetected()
    {
        var vm = await CreateOnModeStepAsync();

        Assert.True(vm.NoServerFound);
        Assert.Null(vm.RecommendedModel);
        Assert.True(vm.CanAdvance);
        vm.NextCommand.Execute(null);
        Assert.Equal(SetupWizardStep.BrowserConnector, vm.Step);
    }

    [Fact]
    public void WelcomeStep_CrashReportsSwitch_StartsFromTheSettingAndSavesChanges()
    {
        _settings.Update(s => s with { CrashReportingEnabled = false });
        var vm = Create();
        Assert.False(vm.CrashReportingEnabled);
        Assert.True(vm.IsCrashReportingAvailable);

        vm.CrashReportingEnabled = true;

        Assert.True(_settings.Current.CrashReportingEnabled);
    }

    [Fact]
    public void WelcomeStep_CrashReportsSwitch_StartsOnByDefault()
    {
        var vm = Create();

        Assert.True(vm.CrashReportingEnabled);
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void WelcomeStep_WithoutAnEndpoint_CrashReportsAreUnavailable()
    {
        _crashReporter.IsAvailable = false;

        var vm = Create();

        Assert.False(vm.IsCrashReportingAvailable);
    }

    [Fact]
    public void WelcomeStep_AFailedSave_PutsTheSwitchBack()
    {
        var vm = Create();
        _settings.UpdateFailure = new IOException("disk full");

        vm.CrashReportingEnabled = false;

        Assert.True(vm.CrashReportingEnabled);
        Assert.True(_settings.Current.CrashReportingEnabled);
        Assert.Contains("disk full", vm.SaveError);
    }

    [Fact]
    public async Task ModeStep_ChoosingCodex_SavesTheMode()
    {
        var vm = await CreateOnModeStepAsync();

        vm.SelectedMode = vm.ModeOptions.Single(option => option.Mode == ProcessingMode.Codex);

        Assert.Equal(ProcessingMode.Codex, _settings.Current.ProcessingMode);
        Assert.True(vm.IsCodexMode);
    }

    [Fact]
    public async Task OnlyReachableServer_IsPreselectedAndItsBestModelSuggested()
    {
        _providers.Servers[ModelProbeService.LmStudioUrl] = FakeProviders.Reachable(
            null,
            FakeProviders.Model("small", "4B", lowQuality: true),
            FakeProviders.Model("large", "27B")
        );

        var vm = await CreateOnModeStepAsync();

        Assert.Equal(ModelProbeService.LmStudioUrl, _settings.Current.LocalServerUrl);
        Assert.Equal("large", vm.RecommendedModel?.Id);
        Assert.Null(vm.ModelAdvice);
        Assert.Equal("large", _settings.Current.LocalModel);
        Assert.True(vm.IsRecommendedModelInUse);
    }

    [Fact]
    public async Task AReopenedWizard_SuggestsTheOnlyServerAndItsModelWithoutSaving()
    {
        _settings.Update(s => s with { HasCompletedOnboarding = true });
        _providers.Servers[ModelProbeService.LmStudioUrl] = FakeProviders.Reachable(null, FakeProviders.Model("large", "27B"));

        var vm = await CreateOnModeStepAsync();

        Assert.Equal(1, _settings.SaveCount);
        Assert.Equal(new AppSettings().LocalServerUrl, _settings.Current.LocalServerUrl);
        var lmStudio = vm.Detections.Single(row => row.Url == ModelProbeService.LmStudioUrl);
        Assert.True(lmStudio.CanUse);

        vm.UseServerCommand.Execute(lmStudio);

        Assert.Equal(ModelProbeService.LmStudioUrl, _settings.Current.LocalServerUrl);
        Assert.Equal("large", vm.RecommendedModel?.Id);
        Assert.False(vm.IsRecommendedModelInUse);
        Assert.Null(_settings.Current.LocalModel);

        vm.UseRecommendedModelCommand.Execute(null);

        Assert.Equal("large", _settings.Current.LocalModel);
    }

    [Fact]
    public async Task AFirstRunInCodexMode_ChoosesNoServerOrModel()
    {
        _settings.Update(s => s with { ProcessingMode = ProcessingMode.Codex });
        _providers.Servers[ModelProbeService.LmStudioUrl] = FakeProviders.Reachable(null, FakeProviders.Model("large", "27B"));

        var vm = await CreateOnModeStepAsync();

        Assert.Equal(1, _settings.SaveCount);
        Assert.Equal(new AppSettings().LocalServerUrl, _settings.Current.LocalServerUrl);
        Assert.Null(_settings.Current.LocalModel);
        Assert.True(vm.Detections.Single(row => row.Url == ModelProbeService.LmStudioUrl).CanUse);
    }

    [Fact]
    public void SettingsChangedElsewhere_AreShownWithoutSavingAgain()
    {
        const string lan = "http://10.0.3.20:11434";
        using var vm = Create();

        _settings.Update(s => s with { ProcessingMode = ProcessingMode.Codex, CrashReportingEnabled = false, LocalServerUrl = lan });

        Assert.Equal(1, _settings.SaveCount);
        Assert.True(vm.IsCodexMode);
        Assert.False(vm.CrashReportingEnabled);
        Assert.Equal(lan, vm.ServerUrl);

        // ReSharper disable once DisposeOnUsingVariable -- disposing is what the test checks; the using covers a failure before it
        vm.Dispose();
        _settings.Update(s => s with { ProcessingMode = ProcessingMode.Local });

        Assert.True(vm.IsCodexMode);
    }

    [Fact]
    public void AModeNoOptionOffers_ShowsLocal()
    {
        _settings.Update(s => s with { ProcessingMode = (ProcessingMode)99 });

        var vm = Create();

        Assert.Equal(ProcessingMode.Local, vm.SelectedMode?.Mode);
        Assert.Equal(1, _settings.SaveCount);
    }

    [Theory]
    [InlineData("http://nas.lan:11434", false)]
    [InlineData("http://nas:11434", false)]
    [InlineData("http://models.example.com:11434", true)]
    public void AServerHostName_OutsideTheLocalSuffixes_ShowsTheHint(string url, bool hint)
    {
        var vm = Create();

        vm.ServerUrl = url;

        Assert.Equal(hint, vm.HasServerNameHint);
    }

    [Fact]
    public async Task ASavedModelTheServerHas_IsKeptOverTheSuggestion()
    {
        _settings.Update(s => s with { LocalModel = "small" });
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable(
            null,
            FakeProviders.Model("small", "4B", lowQuality: true),
            FakeProviders.Model("large", "27B")
        );

        var vm = await CreateOnModeStepAsync();

        Assert.Equal("large", vm.RecommendedModel?.Id);
        Assert.Equal("small", _settings.Current.LocalModel);
        Assert.False(vm.IsRecommendedModelInUse);

        vm.UseRecommendedModelCommand.Execute(null);

        Assert.Equal("large", _settings.Current.LocalModel);
        Assert.True(vm.IsRecommendedModelInUse);
    }

    [Fact]
    public async Task ASavedModelAnotherServerHad_IsReplacedByTheSuggestion()
    {
        const string lan = "http://10.0.3.20:11434";
        _settings.Update(s => s with { LocalModel = "elsewhere" });
        _providers.Servers[lan] = FakeProviders.Reachable("0.12.3", FakeProviders.Model("large", "27B"));
        var vm = await CreateOnModeStepAsync();

        vm.CommitServerUrlCommand.Execute(lan);
        // The suggestion is set last, after the rows report, so it marks the end of the check.
        await WaitUntilAsync(() => vm.Detections.Any(row => row.Url == lan) && vm.HasRecommendation);

        Assert.Equal("large", _settings.Current.LocalModel);
        Assert.True(vm.IsRecommendedModelInUse);
    }

    [Fact]
    public async Task OllamaWithOnlyLowQualityModels_SuggestsPullingABiggerOne()
    {
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable(
            "0.12.3",
            FakeProviders.Model("qwen3:8b", "8.2B", lowQuality: true)
        );

        var vm = await CreateOnModeStepAsync();

        Assert.Equal("qwen3:8b", vm.RecommendedModel?.Id);
        Assert.NotNull(vm.ModelAdvice);
        Assert.Equal(ModelProbeService.OllamaPullCommand, vm.PullCommand);
        Assert.Contains("0.12.3", vm.Detections[0].Summary);
    }

    [Fact]
    public async Task UsingADetectedServer_SavesItsUrl()
    {
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable("0.12.3");
        _providers.Servers[ModelProbeService.LmStudioUrl] = FakeProviders.Reachable(null);
        var vm = await CreateOnModeStepAsync();
        var lmStudio = vm.Detections.Single(row => row.Url == ModelProbeService.LmStudioUrl);

        vm.UseServerCommand.Execute(lmStudio);

        Assert.Equal(ModelProbeService.LmStudioUrl, _settings.Current.LocalServerUrl);
        Assert.True(lmStudio.IsInUse);
        Assert.False(vm.Detections[0].IsSelected);
    }

    [Fact]
    public void ServerAddress_StartsWithTheConfiguredUrl()
    {
        _settings.Update(s => s with { LocalServerUrl = "http://10.0.3.20:11434" });

        var vm = Create();

        Assert.Equal("http://10.0.3.20:11434", vm.ServerUrl);
    }

    [Fact]
    public async Task EnteringAReachableLanAddress_SavesItAndSuggestsItsModel()
    {
        const string lan = "http://10.0.3.20:11434";
        _providers.Servers[lan] = FakeProviders.Reachable("0.12.3", FakeProviders.Model("large", "27B"));
        var vm = await CreateOnModeStepAsync();

        vm.CommitServerUrlCommand.Execute($" {lan} ");
        await WaitUntilAsync(() => vm.Detections.Any(row => row.Url == lan) && vm.HasRecommendation);

        Assert.Equal(lan, _settings.Current.LocalServerUrl);
        Assert.Equal(lan, vm.ServerUrl);
        Assert.Null(vm.ServerUrlError);
        var row = vm.Detections.Single(row => row.Url == lan);
        Assert.Equal(_localizer["Wizard.Mode.YourServer"], row.Title);
        Assert.True(row.IsInUse);
        Assert.Equal("large", vm.RecommendedModel?.Id);
        Assert.Equal("large", _settings.Current.LocalModel);
    }

    [Fact]
    public async Task EnteringAnUnreachableLanAddress_KeepsItOverTheOnlyReachableServer()
    {
        const string lan = "http://10.0.3.20:11434";
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable("0.12.3");
        var vm = await CreateOnModeStepAsync();
        Assert.Equal(ModelProbeService.OllamaUrl, _settings.Current.LocalServerUrl);

        vm.ServerUrl = lan;
        await WaitUntilAsync(() => vm.Detections.All(row => !row.IsBusy) && vm.Detections.Any(row => row.Url == lan));

        Assert.Equal(lan, _settings.Current.LocalServerUrl);
        var row = vm.Detections.Single(row => row.Url == lan);
        Assert.True(row.IsSelected);
        Assert.True(row.NeedsAttention);
        Assert.False(vm.Detections[0].IsSelected);

        // Check again and revisiting the step must not swap it either.
        await vm.DetectModelsCommand.ExecuteAsync(null);
        Assert.Equal(lan, _settings.Current.LocalServerUrl);
        vm.StepIndex = (int)SetupWizardStep.BrowserConnector;
        vm.StepIndex = (int)SetupWizardStep.ProcessingMode;
        await WaitUntilAsync(() => vm.Detections.All(detection => !detection.IsBusy));
        Assert.Equal(lan, _settings.Current.LocalServerUrl);
    }

    [Fact]
    public async Task TheCodexRow_OffersInstallHelpOnlyWhileTheCliIsMissing()
    {
        var vm = await CreateOnModeStepAsync();
        var codex = vm.Detections.Single(row => !row.IsServer);

        Assert.True(codex.IsCodexMissing);
        Assert.DoesNotContain(vm.Detections, row => row is { IsServer: true, IsCodexMissing: true });

        _providers.Codex = new ProviderStatus(ProviderStatusKind.NotSignedIn, "Provider.Status.NotSignedIn", []);
        await vm.DetectModelsCommand.ExecuteAsync(null);

        Assert.False(vm.Detections.Single(row => !row.IsServer).IsCodexMissing);
    }

    [Fact]
    public async Task TheModeStep_RechecksCodexUntilItIsReady()
    {
        _settings.Update(s => s with { ProcessingMode = ProcessingMode.Codex });
        using var vm = Create();
        vm.CodexRecheckInterval = TimeSpan.FromMilliseconds(20);
        vm.StepIndex = (int)SetupWizardStep.ProcessingMode;
        await WaitUntilAsync(() => vm.Detections.Single(row => !row.IsServer).IsCodexMissing);

        _providers.Codex = new ProviderStatus(ProviderStatusKind.NotSignedIn, "Provider.Status.NotSignedIn", []);
        await WaitUntilAsync(() => vm.Detections.Single(row => !row.IsServer).Status?.Kind == ProviderStatusKind.NotSignedIn);

        _providers.Codex = new ProviderStatus(ProviderStatusKind.Ready, "Provider.Status.Ready", []);
        await WaitUntilAsync(() => vm.Detections.Single(row => !row.IsServer).IsSatisfied);
    }

    [Fact]
    public async Task TheModeStep_DoesNotRecheckCodex_InLocalModeOrWhenItNeedsAnUpdate()
    {
        using var vm = Create();
        vm.CodexRecheckInterval = TimeSpan.FromMilliseconds(20);
        vm.StepIndex = (int)SetupWizardStep.ProcessingMode;
        await WaitUntilAsync(() => vm.Detections.Single(row => !row.IsServer).IsCodexMissing);

        _providers.Codex = new ProviderStatus(ProviderStatusKind.Ready, "Provider.Status.Ready", []);
        await Task.Delay(200);
        Assert.True(vm.Detections.Single(row => !row.IsServer).IsCodexMissing);

        _providers.Codex = new ProviderStatus(ProviderStatusKind.Incompatible, "Provider.Status.CodexNeedsUpdate", []);
        vm.SelectedMode = vm.ModeOptions.Single(option => option.Mode == ProcessingMode.Codex);
        await vm.DetectModelsCommand.ExecuteAsync(null);
        _providers.Codex = new ProviderStatus(ProviderStatusKind.Ready, "Provider.Status.Ready", []);
        await Task.Delay(200);
        Assert.Equal(ProviderStatusKind.Incompatible, vm.Detections.Single(row => !row.IsServer).Status?.Kind);
    }

    [Fact]
    public async Task ReenteringTheConfiguredServer_CountsAsChoosingIt()
    {
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable("0.12.3");
        var vm = await CreateOnModeStepAsync();

        vm.ServerUrl = "http://localhost:11434/";
        _providers.Servers.Remove(ModelProbeService.OllamaUrl);
        _providers.Servers[ModelProbeService.LmStudioUrl] = FakeProviders.Reachable(null);
        await vm.DetectModelsCommand.ExecuteAsync(null);

        Assert.Equal(ModelProbeService.OllamaUrl, _settings.Current.LocalServerUrl);
    }

    [Fact]
    public async Task EnterOnTheUnchangedAddress_RechecksItAndCountsAsChoosingIt()
    {
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable("0.12.3");
        var vm = await CreateOnModeStepAsync();
        _providers.Servers.Remove(ModelProbeService.OllamaUrl);
        _providers.Servers[ModelProbeService.LmStudioUrl] = FakeProviders.Reachable(null);

        vm.CommitServerUrlCommand.Execute(vm.ServerUrl);
        await WaitUntilAsync(() => vm.Detections.All(row => !row.IsBusy));

        Assert.True(vm.Detections[0].NeedsAttention);
        Assert.Equal(ModelProbeService.OllamaUrl, _settings.Current.LocalServerUrl);
    }

    [Fact]
    public async Task UsingADetectedServer_ClearsAnEarlierAddressError()
    {
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable("0.12.3");
        var vm = await CreateOnModeStepAsync();
        vm.ServerUrl = "not a url";
        Assert.True(vm.HasServerUrlError);

        vm.UseServerCommand.Execute(vm.Detections[0]);

        Assert.False(vm.HasServerUrlError);
        Assert.Equal(ModelProbeService.OllamaUrl, vm.ServerUrl);
    }

    [Fact]
    public async Task AConfiguredLanAddress_IsKeptByANewWizard()
    {
        const string lan = "http://10.0.3.20:11434";
        _settings.Update(s => s with { LocalServerUrl = lan });
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable("0.12.3");

        var vm = await CreateOnModeStepAsync();

        Assert.Equal(lan, _settings.Current.LocalServerUrl);
        Assert.True(vm.Detections.Single(row => row.Url == lan).IsSelected);
    }

    [Fact]
    public async Task SavingAModel_KeepsAnAddressDraftAndItsError()
    {
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable("0.12.3", FakeProviders.Model("large", "27B"));
        var vm = await CreateOnModeStepAsync();
        vm.ServerUrl = "not a url";

        vm.UseRecommendedModelCommand.Execute(null);

        Assert.Equal("large", _settings.Current.LocalModel);
        Assert.Equal("not a url", vm.ServerUrl);
        Assert.True(vm.HasServerUrlError);
    }

    [Fact]
    public async Task AServerChosenWithUse_IsKeptWhenItGoesDown()
    {
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable("0.12.3");
        _providers.Servers[ModelProbeService.LmStudioUrl] = FakeProviders.Reachable(null);
        var vm = await CreateOnModeStepAsync();
        vm.UseServerCommand.Execute(vm.Detections.Single(row => row.Url == ModelProbeService.LmStudioUrl));

        _providers.Servers.Remove(ModelProbeService.LmStudioUrl);
        await vm.DetectModelsCommand.ExecuteAsync(null);

        Assert.Equal(ModelProbeService.LmStudioUrl, _settings.Current.LocalServerUrl);
    }

    [Fact]
    public async Task EnteringAnInvalidAddress_ShowsTheErrorAndSavesNothing()
    {
        var vm = await CreateOnModeStepAsync();
        var before = _settings.Current.LocalServerUrl;
        var rows = vm.Detections.ToList();

        vm.CommitServerUrlCommand.Execute("not a url");

        Assert.Equal(_localizer["Models.Status.InvalidUrl"], vm.ServerUrlError);
        Assert.True(vm.HasServerUrlError);
        Assert.Equal(before, _settings.Current.LocalServerUrl);
        Assert.Equal(rows, vm.Detections);
        Assert.False(vm.IsDetecting);
    }

    [Fact]
    public async Task UsingADetectedServer_UpdatesTheAddressField()
    {
        _providers.Servers[ModelProbeService.OllamaUrl] = FakeProviders.Reachable("0.12.3");
        _providers.Servers[ModelProbeService.LmStudioUrl] = FakeProviders.Reachable(null);
        var vm = await CreateOnModeStepAsync();

        vm.UseServerCommand.Execute(vm.Detections.Single(row => row.Url == ModelProbeService.LmStudioUrl));

        Assert.Equal(ModelProbeService.LmStudioUrl, vm.ServerUrl);
    }

    private static FakeSetupTask NewTask(string id, SetupTaskSeverity severity, SetupTaskStatusKind kind)
    {
        return new FakeSetupTask(id, severity, kind);
    }

    [Fact]
    public void DoneBody_NamesTheShortcutRecordedInTheWizard()
    {
        var vm = Create();
        _settings.Update(s => s with { RewriteShortcut = "Ctrl+Alt+J" });

        vm.StepIndex = (int)SetupWizardStep.Done;

        // Shown in the OS's own form: Cmd on macOS, Ctrl elsewhere.
        Assert.True(KeyChord.TryParse("Ctrl+Alt+J", out var chord));
        Assert.Contains(chord.ToDisplayString(OperatingSystem.IsMacOS()), vm.DoneBody);
    }

    /// <summary>The panel opens idle, so the rewrite shows once the user picks an action there.</summary>
    private async Task LoadAndRunInPanelAsync()
    {
        await _panel.LoadAsync(new FakeRewriteTarget(), Capture());
        await _panel.SelectActionCommand.ExecuteAsync(_panel.Actions[0]);
    }

    /// <summary>Shows a menu over a capture through the fake extension and waits until it is reported shown; returns its id.</summary>
    private async Task<string> ShowMenuAsync()
    {
        var shown = new TaskCompletionSource();
        Action onShown = () => shown.TrySetResult();
        _menus.MenuShown += onShown;
        _menuChannel.Deliver(Hello);
        await NextMenuMessageAsync("config");
        _menus.OnCaptureCompleted(_menuSession, 1, Capture());
        var show = await NextMenuMessageAsync("menu.show");
        _menuChannel.Deliver($$"""{"v":1,"type":"menu.response","id":"{{show.GetProperty("id").GetString()}}","ok":true}""");
        await shown.Task.WaitAsync(s_timeout);
        _menus.MenuShown -= onShown;
        return show.GetProperty("menuId").GetString()!;
    }

    private async Task<JsonElement> NextMenuMessageAsync(string type)
    {
        var message = await _menuChannel.NextSentAsync();
        Assert.Equal(type, message.GetProperty("type").GetString());
        return message;
    }

    private SetupWizardViewModel Create(params FakeSetupTask[] tasks)
    {
        var connectorPanel = new BrowserConnectorPanelViewModel(_registrar, _connector, _localizer, action => action());
        var hotkeys = new HotkeyService([], _settings, _connector);
        var shortcut = new ShortcutPanelViewModel(hotkeys, _settings, _localizer, action => action());
        return new SetupWizardViewModel(
            _settings,
            _localizer,
            _providers.CreateService(_localizer),
            connectorPanel,
            shortcut,
            _connector,
            _panel,
            _menus,
            _crashReporter,
            tasks,
            action => action()
        );
    }

    private async Task<Stream> ConnectAndSayHelloAsync()
    {
        var stream = await _socket.ConnectAsync(s_timeout);
        Assert.NotNull(stream);
        await WriteAsync(stream, Hello);
        Assert.Equal("config", (await ReadAsync(stream)).GetProperty("type").GetString());
        await WaitUntilAsync(() => _connector.Clients.Count > 0);
        return stream;
    }

    private static ContextResult Capture()
    {
        var context = new RewriteContext("hi dana", [], null, ThreadCompleteness.DraftOnly, 0);
        return new ContextResult(new CapturedContext(context, "test-page", "e-1", "sha256:ab", null, 0, 7), null);
    }

    private static string CaptureResponse(string id) =>
        $$$"""
        {"v":1,"type":"context.response","id":"{{{id}}}","ok":true,"capture":{
          "adapter":"test-page","editor":{"id":"e-1","hash":"sha256:ab"},
          "selection":{"text":"hi dana","start":0,"end":7},
          "thread":{"completeness":"draft-only","messages":[]}}
        }
        """;

    private static Task WriteAsync(Stream stream, string json) =>
        NativeMessagingFraming.WriteFrameAsync(stream, json, CancellationToken.None);

    private static async Task<JsonElement> ReadAsync(Stream stream)
    {
        var json = await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout);
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    private async Task<SetupWizardViewModel> CreateOnModeStepAsync()
    {
        var vm = Create();
        vm.StepIndex = (int)SetupWizardStep.ProcessingMode;
        await vm.DetectModelsCommand.ExecuteAsync(null);
        return vm;
    }

    private async Task<SetupWizardViewModel> CreateOnLastStepAsync(params FakeSetupTask[] tasks)
    {
        var vm = Create(tasks);
        vm.StepIndex = SetupWizardViewModel.StepCount - 1;
        await vm.RecheckSetupCommand.ExecuteAsync(null);
        return vm;
    }
}
