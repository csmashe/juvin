using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Platform.Setup;

namespace Juvin.App.ViewModels;

/// <summary>
///     Order of the wizard pages. The view has one tab per member, in the same order. Processing
///     mode, browser connector, shortcut, and try-it steps are added here as their phases land.
/// </summary>
public enum SetupWizardStep
{
    // ReSharper disable once UnusedMember.Global -- step 0, reached through StepIndex
    Welcome,
    Checklist,
    Done,
}

/// <summary>
///     First-run setup. The checklist step renders every <see cref="ISetupTask" /> that applies to
///     this machine; Finish stays disabled until all required tasks are satisfied, and Skip is the
///     explicit way past them.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Localizer _localizer;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private int _stepIndex;

    [ObservableProperty]
    private string? _saveError;

    public SetupWizardViewModel(
        ISettingsService settings,
        Localizer localizer,
        IEnumerable<ISetupTask> setupTasks
    )
    {
        _settings = settings;
        _localizer = localizer;
        SetupItems =
        [
            .. setupTasks
                .Where(task => task.AppliesToThisMachine())
                .Select(task => new SetupTaskRow(task, localizer)),
        ];
        StepDots = [.. Enumerable.Range(0, StepCount).Select(index => new StepDot(index))];
        DoneBody = localizer.Format("Wizard.Done.Body", settings.Current.RewriteShortcut);
        RefreshStepState();
    }

    public event EventHandler? RequestClose;

    public static int StepCount { get; } = Enum.GetValues<SetupWizardStep>().Length;

    public ObservableCollection<SetupTaskRow> SetupItems { get; }
    public ObservableCollection<StepDot> StepDots { get; }
    public string DoneBody { get; }

    public SetupWizardStep Step => (SetupWizardStep)StepIndex;
    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == StepCount - 1;
    public bool HasSetupItems => SetupItems.Count > 0;
    public string NextLabel => _localizer[IsLastStep ? "Common.Finish" : "Common.Next"];
    public string StepText => _localizer.Format("Wizard.StepText", StepIndex + 1, StepCount);

    public bool AllRequiredReady => SetupItems.Where(row => row.IsRequired).All(row => row.IsSatisfied);

    public bool CanAdvance => !IsLastStep || AllRequiredReady;

    private bool IsClosed => _lifetime.IsCancellationRequested;

    public void Dispose()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
    }

    // ReSharper disable once UnusedParameterInPartialMethod -- signature fixed by the source generator
    partial void OnStepIndexChanged(int value)
    {
        RefreshStepState();
        if (Step is SetupWizardStep.Checklist or SetupWizardStep.Done)
        {
            _ = RefreshSetupAsync();
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsFirstStep)
        {
            StepIndex--;
        }
    }

    [RelayCommand]
    private void Next()
    {
        if (!IsLastStep)
        {
            StepIndex++;
            return;
        }

        // Guards the gate even if the command fires while the button is disabled.
        if (AllRequiredReady)
        {
            Complete();
        }
    }

    [RelayCommand]
    private void Skip()
    {
        Complete();
    }

    [RelayCommand]
    private Task RecheckSetupAsync()
    {
        return RefreshSetupAsync();
    }

    [RelayCommand]
    private async Task RunSetupActionAsync(SetupTaskRow? row)
    {
        if (IsClosed || row is null || row.IsBusy)
        {
            return;
        }

        row.BeginAction();
        RefreshGating();

        SetupActionOutcome outcome;
        try
        {
            var token = _lifetime.Token;
            outcome = await Task.Run(() => row.Source.RunActionAsync(token), token);
        }
        catch (OperationCanceledException) when (IsClosed)
        {
            return;
        }
        catch (Exception ex)
        {
            outcome = new SetupActionOutcome(false, _localizer.Format("Wizard.Checklist.ActionFailed", ex.Message));
        }

        if (IsClosed)
        {
            return;
        }

        row.EndAction(outcome);

        // One fix can satisfy several tasks, so every row is re-evaluated.
        await RefreshSetupAsync();
    }

    private async Task RefreshSetupAsync()
    {
        foreach (var row in SetupItems.Where(row => !row.IsBusy).ToList())
        {
            if (IsClosed)
            {
                return;
            }

            SetupTaskState state;
            try
            {
                var token = _lifetime.Token;
                state = await Task.Run(() => row.Source.EvaluateAsync(token), token);
            }
            catch (OperationCanceledException) when (IsClosed)
            {
                return;
            }
            catch (Exception ex)
            {
                state = new SetupTaskState(
                    SetupTaskStatusKind.Failed,
                    _localizer.Format("Wizard.Checklist.CheckFailed", ex.Message)
                );
            }

            if (IsClosed)
            {
                return;
            }

            row.Apply(state);
        }

        RefreshGating();
    }

    private void Complete()
    {
        if (IsClosed)
        {
            return;
        }

        try
        {
            _settings.Update(s => s with { HasCompletedOnboarding = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = _localizer.Format("Wizard.SaveFailed", ex.Message);
            return;
        }

        SaveError = null;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshStepState()
    {
        foreach (var dot in StepDots)
        {
            dot.IsActive = dot.Index == StepIndex;
        }

        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextLabel));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(CanAdvance));
    }

    private void RefreshGating()
    {
        OnPropertyChanged(nameof(AllRequiredReady));
        OnPropertyChanged(nameof(CanAdvance));
    }
}

public sealed partial class StepDot : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public StepDot(int index)
    {
        Index = index;
    }

    public int Index { get; }
}

/// <summary>
///     One checklist row wrapping an <see cref="ISetupTask" />. The action message is kept apart
///     from the evaluated state so it survives the re-evaluation that follows each action.
/// </summary>
public sealed partial class SetupTaskRow : ObservableObject
{
    private readonly Localizer _localizer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAction))]
    private string? _actionLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionMessage))]
    private string? _actionMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopyCommand))]
    private string? _copyCommand;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAction))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSatisfied), nameof(NeedsAttention), nameof(StatusSymbol))]
    private SetupTaskStatusKind _kind = SetupTaskStatusKind.Working;

    [ObservableProperty]
    private string _summary;

    public SetupTaskRow(ISetupTask source, Localizer localizer)
    {
        _localizer = localizer;
        Source = source;
        Title = source.Title;
        IsRequired = source.Severity == SetupTaskSeverity.Required;
        RequirementLabel = localizer[IsRequired ? "Setup.Required" : "Setup.Recommended"];
        _summary = localizer["Wizard.Checklist.Checking"];
    }

    public ISetupTask Source { get; }
    public string Title { get; }
    public bool IsRequired { get; }
    public string RequirementLabel { get; }

    public bool IsSatisfied => Kind == SetupTaskStatusKind.Satisfied;
    public bool NeedsAttention => Kind is SetupTaskStatusKind.NeedsAction or SetupTaskStatusKind.Failed;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasCopyCommand => !string.IsNullOrWhiteSpace(CopyCommand);
    public bool HasActionMessage => !string.IsNullOrWhiteSpace(ActionMessage);
    public bool CanRunAction => !IsBusy && !string.IsNullOrWhiteSpace(ActionLabel);

    public Symbol StatusSymbol =>
        Kind switch
        {
            SetupTaskStatusKind.Satisfied => Symbol.CheckmarkCircle,
            SetupTaskStatusKind.Failed => Symbol.ErrorCircle,
            SetupTaskStatusKind.Working => Symbol.ArrowSync,
            _ => Symbol.Warning,
        };

    public void Apply(SetupTaskState state)
    {
        Kind = state.Kind;
        Summary = state.Summary;
        Detail = state.Detail;
        ActionLabel = state.ActionLabel;
        CopyCommand = state.CopyCommand;
    }

    public void BeginAction()
    {
        IsBusy = true;
        Kind = SetupTaskStatusKind.Working;
        ActionMessage = _localizer["Wizard.Checklist.Working"];
    }

    public void EndAction(SetupActionOutcome outcome)
    {
        IsBusy = false;
        ActionMessage = string.IsNullOrWhiteSpace(outcome.Detail)
            ? outcome.Message
            : $"{outcome.Message} {outcome.Detail}";
    }
}
