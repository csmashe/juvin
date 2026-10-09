using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels;
using Juvin.Core.Localization;
using Juvin.Platform.Setup;

namespace Juvin.App.Tests;

public sealed class SetupWizardViewModelTests
{
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

        Assert.Equal(SetupWizardStep.Checklist, vm.Step);
        Assert.False(_settings.Current.HasCompletedOnboarding);
    }

    private static FakeSetupTask NewTask(string id, SetupTaskSeverity severity, SetupTaskStatusKind kind)
    {
        return new FakeSetupTask(id, severity, kind);
    }

    private SetupWizardViewModel Create(params FakeSetupTask[] tasks)
    {
        return new SetupWizardViewModel(_settings, new Localizer(), tasks);
    }

    private async Task<SetupWizardViewModel> CreateOnLastStepAsync(params FakeSetupTask[] tasks)
    {
        var vm = Create(tasks);
        vm.StepIndex = SetupWizardViewModel.StepCount - 1;
        await vm.RecheckSetupCommand.ExecuteAsync(null);
        return vm;
    }
}
