using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels;
using Juvin.Core.Hotkeys;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Platform;
using Juvin.Platform.Setup;

namespace Juvin.App.Tests;

public sealed class SetupWizardViewModelTests
{
    private readonly FakeProviders _providers = new();
    private readonly FakeNativeHostRegistrar _registrar = new();
    private readonly FakeSettingsService _settings = new();

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
    public void Steps_RunWelcomeModeBrowserShortcutChecklistDone()
    {
        Assert.Equal(6, SetupWizardViewModel.StepCount);
        Assert.Equal(1, (int)SetupWizardStep.ProcessingMode);
        Assert.Equal(SetupWizardStep.BrowserConnector, SetupWizardStep.ProcessingMode + 1);
        Assert.Equal(SetupWizardStep.Shortcut, SetupWizardStep.BrowserConnector + 1);
        Assert.Equal(SetupWizardStep.Checklist, SetupWizardStep.Shortcut + 1);
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
        _settings.Update(s => s with { LocalServerUrl = "http://10.0.3.20:11434" });
        _providers.Servers[ModelProbeService.LmStudioUrl] = FakeProviders.Reachable(
            null,
            FakeProviders.Model("small", "4B", lowQuality: true),
            FakeProviders.Model("large", "27B")
        );

        var vm = await CreateOnModeStepAsync();

        Assert.Equal(ModelProbeService.LmStudioUrl, _settings.Current.LocalServerUrl);
        Assert.Equal("large", vm.RecommendedModel?.Id);
        Assert.Null(vm.ModelAdvice);

        vm.UseRecommendedModelCommand.Execute(null);

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

    private SetupWizardViewModel Create(params FakeSetupTask[] tasks)
    {
        var localizer = new Localizer();
        var connector = new ConnectorService(new FakeConnectorSocket(), _registrar, _settings, localizer);
        var panel = new BrowserConnectorPanelViewModel(_registrar, connector, localizer);
        var hotkeys = new HotkeyService([], _settings, connector);
        var shortcut = new ShortcutPanelViewModel(hotkeys, _settings, localizer, action => action());
        return new SetupWizardViewModel(_settings, localizer, _providers.CreateService(localizer), panel, shortcut, tasks);
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
