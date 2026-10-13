using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Juvin.App.Services;
using Juvin.Core.Connector;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Platform.Setup;

namespace Juvin.App.ViewModels;

/// <summary>Order of the wizard pages. The view has one tab per member, in the same order.</summary>
public enum SetupWizardStep
{
    // ReSharper disable once UnusedMember.Global -- step 0, reached through StepIndex
    Welcome,
    ProcessingMode,
    BrowserConnector,
    Shortcut,
    Checklist,
    TryIt,
    Done,
}

/// <summary>How far the user got through the try-it step's sample rewrite.</summary>
public enum TryItPhase
{
    NotStarted,
    SampleOpened,
    Captured,
    RewriteShown,
    CaptureFailed,
}

/// <summary>
///     First-run setup. The checklist step renders every <see cref="ISetupTask" /> that applies to
///     this machine; Finish stays disabled until all required tasks are satisfied, and Skip is the
///     explicit way past them.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan s_openSampleTimeout = TimeSpan.FromSeconds(5);

    private readonly ConnectorService _connector;
    private readonly ProbeGate _detection = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Localizer _localizer;
    private readonly RewritePanelViewModel _panel;
    private readonly Action<Action> _post;
    private readonly ModelProbeService _probes;
    private readonly ISettingsService _settings;
    private bool _applyingMode;
    private bool _applyingServerUrl;
    private bool _serverChosenByUser;

    // Read and written on the UI thread only; captures post in either order, so an older one is dropped.
    private long _shownCapture;

    // Counts every capture outcome the step saw, so a pending Open sample knows whether one arrived meanwhile.
    private int _captureArrivals;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCodexMode), nameof(ModeHint))]
    private ProcessingModeOption? _selectedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelPanel))]
    private bool _isDetecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendation), nameof(ShowModelPanel))]
    private ModelOption? _recommendedModel;

    [ObservableProperty]
    private bool _isRecommendedModelInUse;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelAdvice), nameof(ShowModelPanel))]
    private string? _modelAdvice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPullCommand))]
    private string? _pullCommand;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelPanel))]
    private bool _noServerFound;

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerUrlError))]
    private string? _serverUrlError;

    [ObservableProperty]
    private int _stepIndex;

    [ObservableProperty]
    private string? _saveError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenSample))]
    private bool _isBrowserConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenSample))]
    private bool _isOpeningSample;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SampleOpenedSymbol))]
    private bool _isSampleOpened;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCaptured), nameof(IsRewriteShown), nameof(CapturedSymbol), nameof(RewriteShownSymbol))]
    private TryItPhase _tryItPhase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTryItStatus))]
    private string? _tryItStatus;

    public SetupWizardViewModel(
        ISettingsService settings,
        Localizer localizer,
        ModelProbeService probes,
        BrowserConnectorPanelViewModel connector,
        ShortcutPanelViewModel shortcut,
        ConnectorService connectorService,
        RewritePanelViewModel panel,
        IEnumerable<ISetupTask> setupTasks,
        Action<Action> post
    )
    {
        _settings = settings;
        _connector = connectorService;
        _panel = panel;
        _post = post;
        Connector = connector;
        Shortcut = shortcut;
        _localizer = localizer;
        _probes = probes;
        ModeOptions = ProcessingModeOption.All(localizer);
        _applyingMode = true;
        SelectedMode = ModeOptions.First(option => option.Mode == settings.Current.ProcessingMode);
        _applyingMode = false;
        _serverUrl = settings.Current.LocalServerUrl;
        SetupItems =
        [
            .. setupTasks
                .Where(task => task.AppliesToThisMachine())
                .Select(task => new SetupTaskRow(task, localizer)),
        ];
        StepDots = [.. Enumerable.Range(0, StepCount).Select(index => new StepDot(index))];
        RefreshStepState();

        // The connector and the panel outlive the wizard, so Dispose unsubscribes.
        _connector.ClientsChanged += OnClientsChanged;
        _connector.CaptureCompleted += OnCaptureCompleted;
        _connector.TriggerFailed += OnTriggerFailed;
        _panel.CaptureLoaded += OnPanelCaptureLoaded;
        _panel.PropertyChanged += OnPanelPropertyChanged;
        RefreshBrowserConnection();
    }

    public event EventHandler? RequestClose;

    public static int StepCount { get; } = Enum.GetValues<SetupWizardStep>().Length;

    public BrowserConnectorPanelViewModel Connector { get; }
    public ShortcutPanelViewModel Shortcut { get; }
    public ObservableCollection<SetupTaskRow> SetupItems { get; }
    public IReadOnlyList<ProcessingModeOption> ModeOptions { get; }
    public ObservableCollection<DetectionRow> Detections { get; } = [];
    public ObservableCollection<StepDot> StepDots { get; }
    public string DoneBody => _localizer.Format("Wizard.Done.Body", Shortcut.ShortcutText);
    public string TryItBody => _localizer.Format("Wizard.TryIt.Body", Shortcut.ShortcutText);
    public bool CanOpenSample => IsBrowserConnected && !IsOpeningSample;
    public bool IsCaptured => TryItPhase is TryItPhase.Captured or TryItPhase.RewriteShown;
    public bool IsRewriteShown => TryItPhase == TryItPhase.RewriteShown;
    public bool HasTryItStatus => !string.IsNullOrWhiteSpace(TryItStatus);
    public Symbol SampleOpenedSymbol => ProgressSymbol(IsSampleOpened);
    public Symbol CapturedSymbol => ProgressSymbol(IsCaptured);
    public Symbol RewriteShownSymbol => ProgressSymbol(IsRewriteShown);

    public SetupWizardStep Step => (SetupWizardStep)StepIndex;
    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == StepCount - 1;
    public bool HasSetupItems => SetupItems.Count > 0;
    public bool IsCodexMode => SelectedMode?.Mode == ProcessingMode.Codex;
    public string ModeHint => SelectedMode?.Hint ?? "";
    public bool HasRecommendation => RecommendedModel is not null;
    public bool HasModelAdvice => !string.IsNullOrWhiteSpace(ModelAdvice);
    public bool HasPullCommand => !string.IsNullOrWhiteSpace(PullCommand);
    public bool HasServerUrlError => !string.IsNullOrWhiteSpace(ServerUrlError);
    public bool ShowModelPanel => !IsDetecting && (HasRecommendation || HasModelAdvice || NoServerFound);
    public string NextLabel => _localizer[IsLastStep ? "Common.Finish" : "Common.Next"];
    public string StepText => _localizer.Format("Wizard.StepText", StepIndex + 1, StepCount);

    public bool AllRequiredReady => SetupItems.Where(row => row.IsRequired).All(row => row.IsSatisfied);

    public bool CanAdvance => !IsLastStep || AllRequiredReady;

    private bool IsClosed => _lifetime.IsCancellationRequested;

    public void Dispose()
    {
        _connector.ClientsChanged -= OnClientsChanged;
        _connector.CaptureCompleted -= OnCaptureCompleted;
        _connector.TriggerFailed -= OnTriggerFailed;
        _panel.CaptureLoaded -= OnPanelCaptureLoaded;
        _panel.PropertyChanged -= OnPanelPropertyChanged;
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
        Connector.Dispose();
        Shortcut.Dispose();
    }

    // ReSharper disable once UnusedParameterInPartialMethod -- signature fixed by the source generator
    partial void OnStepIndexChanged(int value)
    {
        RefreshStepState();
        if (Step == SetupWizardStep.BrowserConnector)
        {
            Connector.DetectCommand.Execute(null);
        }

        if (Step == SetupWizardStep.TryIt)
        {
            RefreshBrowserConnection();
        }

        _ = Step switch
        {
            SetupWizardStep.ProcessingMode => DetectModelsAsync(),
            SetupWizardStep.Checklist or SetupWizardStep.Done => RefreshSetupAsync(),
            _ => Task.CompletedTask,
        };
    }

    partial void OnSelectedModeChanged(ProcessingModeOption? value)
    {
        if (!_applyingMode && value is not null)
        {
            SaveSetting(s => s with { ProcessingMode = value.Mode });
        }
    }

    partial void OnServerUrlChanged(string value)
    {
        if (!_applyingServerUrl)
        {
            ApplyServerUrl(value);
        }
    }

    // The setter skips an unchanged value, but Enter must still confirm and recheck it.
    [RelayCommand]
    private void CommitServerUrl(string? text)
    {
        var value = text ?? "";
        if (value == ServerUrl)
        {
            ApplyServerUrl(value);
        }
        else
        {
            ServerUrl = value;
        }
    }

    private void ApplyServerUrl(string value)
    {
        var url = value.Trim();
        if (_probes.IsThisDevice(url) is null)
        {
            ServerUrlError = _localizer["Models.Status.InvalidUrl"];
            return;
        }

        ServerUrlError = null;
        _serverChosenByUser = true;
        if (!ModelProbeService.IsSameServer(url, _settings.Current.LocalServerUrl))
        {
            SaveSetting(s => s with { LocalServerUrl = url });
        }

        _ = DetectModelsAsync();
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

    [RelayCommand]
    private async Task OpenSampleAsync()
    {
        if (IsClosed || IsOpeningSample)
        {
            return;
        }

        if (_connector.Clients.Count == 0)
        {
            TryItStatus = _localizer["Wizard.TryIt.NoBrowser"];
            return;
        }

        IsOpeningSample = true;
        TryItStatus = _localizer["Wizard.TryIt.Opening"];
        var arrivalsBefore = _captureArrivals;
        ConnectorError? error;
        try
        {
            error = await _connector.OpenTestPageAsync(s_openSampleTimeout, _lifetime.Token);
        }
        catch (OperationCanceledException) when (IsClosed)
        {
            return;
        }
        finally
        {
            if (!IsClosed)
            {
                IsOpeningSample = false;
            }
        }

        if (IsClosed)
        {
            return;
        }

        if (error is not null)
        {
            // A capture that landed while the reply was in flight outranks the failure.
            if (_captureArrivals == arrivalsBefore)
            {
                TryItStatus = _localizer.Format("Wizard.TryIt.OpenFailed", _localizer[RewritePanelViewModel.ErrorKeyFor(error.Code)]);
            }

            return;
        }

        // Rows only move forward: a capture that landed while the reply was in flight stays ticked.
        IsSampleOpened = true;
        if (TryItPhase is TryItPhase.NotStarted or TryItPhase.CaptureFailed)
        {
            TryItPhase = TryItPhase.SampleOpened;
        }

        TryItStatus = TryItPhase == TryItPhase.SampleOpened ? _localizer["Wizard.TryIt.Waiting"] : null;
    }

    [RelayCommand]
    private void UseServer(DetectionRow? row)
    {
        if (row?.Url is null || !row.IsReachable)
        {
            return;
        }

        _serverChosenByUser = true;
        SaveSetting(s => s with { LocalServerUrl = row.Url });
        ShowConfiguredServer();
        RefreshServerChoice();
    }

    [RelayCommand]
    private void UseRecommendedModel()
    {
        if (RecommendedModel is not { } model)
        {
            return;
        }

        SaveSetting(s => s with { LocalModel = model.Id });
        RefreshServerChoice();
    }

    [RelayCommand]
    private async Task DetectModelsAsync()
    {
        if (IsClosed)
        {
            return;
        }

        var settings = _settings.Current;
        Detections.Clear();
        foreach (var row in DetectionCandidates(settings.LocalServerUrl))
        {
            Detections.Add(row);
        }

        var rows = Detections.ToList();
        IsDetecting = true;
        var statuses = await _detection.RunAsync(
            ct => Task.WhenAll(
                rows.Select(row => row.Url is null
                    ? _probes.ProbeCodexAsync(settings, ct)
                    : _probes.ProbeLocalAsync(settings, row.Url, ct))
            ),
            _lifetime.Token
        );
        if (statuses is null || IsClosed)
        {
            return;
        }

        IsDetecting = false;
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Apply(statuses[i], DescribeDetection(rows[i], statuses[i]));
        }

        var reachable = rows.Where(row => row is { IsServer: true, IsReachable: true }).ToList();
        // A server the user typed or chose is never swapped for the only running one, even while it is down.
        // An address that is not a built-in default was set by the user, in this wizard or an earlier one or in Models.
        if (
            !_serverChosenByUser
            && IsDefaultServer(settings.LocalServerUrl)
            && reachable.Count == 1
            && !ModelProbeService.IsSameServer(reachable[0].Url!, settings.LocalServerUrl)
        )
        {
            SaveSetting(s => s with { LocalServerUrl = reachable[0].Url! });
        }

        RefreshServerChoice();
    }

    private static bool IsDefaultServer(string url) =>
        ModelProbeService.IsSameServer(url, ModelProbeService.OllamaUrl) || ModelProbeService.IsSameServer(url, ModelProbeService.LmStudioUrl);

    private IEnumerable<DetectionRow> DetectionCandidates(string configuredUrl)
    {
        yield return new DetectionRow(_localizer["Wizard.Mode.Ollama"], ModelProbeService.OllamaUrl, _localizer["Wizard.Checklist.Checking"]);
        yield return new DetectionRow(_localizer["Wizard.Mode.LmStudio"], ModelProbeService.LmStudioUrl, _localizer["Wizard.Checklist.Checking"]);
        if (!IsDefaultServer(configuredUrl))
        {
            yield return new DetectionRow(_localizer["Wizard.Mode.YourServer"], configuredUrl, _localizer["Wizard.Checklist.Checking"]);
        }

        yield return new DetectionRow(_localizer["Models.Codex"], null, _localizer["Wizard.Checklist.Checking"]);
    }

    private string DescribeDetection(DetectionRow row, ProviderStatus status)
    {
        if (!row.IsServer)
        {
            return _probes.DescribeCodex(status);
        }

        if (!ModelProbeService.IsReachable(status))
        {
            return row.Url is ModelProbeService.OllamaUrl or ModelProbeService.LmStudioUrl
                ? _localizer.Format("Wizard.Mode.NotRunning", row.Title)
                : _localizer[status.MessageKey];
        }

        var name = row.Url == ModelProbeService.LmStudioUrl ? row.Title : _probes.ServerName(status);
        var key = _probes.IsThisDevice(row.Url!) == true ? "Wizard.Mode.FoundHere" : "Wizard.Mode.FoundPrivate";
        return _localizer.Format(key, name, _probes.ModelCount(status.Models.Count));
    }

    /// <summary>Marks the server in use and suggests a model from it.</summary>
    private void RefreshServerChoice()
    {
        var settings = _settings.Current;
        foreach (var row in Detections.Where(row => row.IsServer))
        {
            row.IsSelected = ModelProbeService.IsSameServer(row.Url!, settings.LocalServerUrl);
        }

        var server = Detections.FirstOrDefault(row => row is { IsSelected: true, IsReachable: true });
        NoServerFound = !Detections.Any(row => row is { IsServer: true, IsReachable: true });
        if (server?.Status is not { } status)
        {
            RecommendedModel = null;
            IsRecommendedModelInUse = false;
            ModelAdvice = null;
            PullCommand = null;
            return;
        }

        var recommended = ModelProbeService.RecommendModel(status.Models);
        RecommendedModel = recommended is null ? null : ModelOption.From(recommended);
        IsRecommendedModelInUse = recommended is not null && ModelProbeService.IsSameModel(recommended.Id, settings.LocalModel);
        var needsBetterModel = recommended is null || recommended.IsLowQualityClass;
        ModelAdvice = needsBetterModel
            ? _localizer[recommended is null ? "Wizard.Mode.NoModels" : "Wizard.Mode.OnlyLowQuality"]
            : null;
        PullCommand = needsBetterModel && ModelProbeService.IsOllama(status) ? ModelProbeService.OllamaPullCommand : null;
    }

    private static Symbol ProgressSymbol(bool reached) => reached ? Symbol.CheckmarkCircle : Symbol.Circle;

    private void OnClientsChanged() => _post(RefreshBrowserConnection);

    private void OnCaptureCompleted(ConnectorSession session, long number, ContextResult result) => _post(() => ShowCapture(number, result));

    private void OnTriggerFailed(long number, ContextResult result) => _post(() => ShowCapture(number, result));

    private void RefreshBrowserConnection()
    {
        if (!IsClosed)
        {
            IsBrowserConnected = _connector.Clients.Count > 0;
        }
    }

    /// <summary>Any adapter counts, so a capture from Gmail ticks the row as well as one from the sample page.</summary>
    private void ShowCapture(long number, ContextResult result)
    {
        if (IsClosed || number < _shownCapture)
        {
            return;
        }

        _shownCapture = number;
        _captureArrivals++;
        if (result.Ok)
        {
            // The panel can reach Done before this callback runs; a rewrite already shown stays shown.
            if (!IsRewriteShown)
            {
                TryItPhase = TryItPhase.Captured;
            }

            TryItStatus = null;
            return;
        }

        if (!IsCaptured)
        {
            TryItPhase = TryItPhase.CaptureFailed;
        }

        TryItStatus = _localizer.Format("Wizard.TryIt.CaptureFailed", _localizer[RewritePanelViewModel.ErrorKeyFor(result.Error?.Code)]);
    }

    /// <summary>
    ///     The panel also moves the rows forward, because a Recapture from the panel goes straight to
    ///     the session and never raises <see cref="ConnectorService.CaptureCompleted" />.
    /// </summary>
    private void OnPanelCaptureLoaded()
    {
        if (IsClosed)
        {
            return;
        }

        _captureArrivals++;
        if (!IsCaptured)
        {
            TryItPhase = TryItPhase.Captured;
        }

        TryItStatus = null;
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsClosed || e.PropertyName != nameof(RewritePanelViewModel.State) || _panel.State != RewritePanelState.Done)
        {
            return;
        }

        TryItPhase = TryItPhase.RewriteShown;
        TryItStatus = null;
    }

    private void SaveSetting(Func<AppSettings, AppSettings> update)
    {
        var previousUrl = _settings.Current.LocalServerUrl;
        try
        {
            _settings.Update(update);
            SaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = _localizer.Format("Wizard.SaveFailed", ex.Message);
            _applyingMode = true;
            SelectedMode = ModeOptions.First(option => option.Mode == _settings.Current.ProcessingMode);
            _applyingMode = false;
        }

        // Only a server change touches the address field, so saving a model keeps a draft and its error.
        if (_settings.Current.LocalServerUrl != previousUrl)
        {
            ShowConfiguredServer();
        }
    }

    private void ShowConfiguredServer()
    {
        _applyingServerUrl = true;
        ServerUrl = _settings.Current.LocalServerUrl;
        _applyingServerUrl = false;
        ServerUrlError = null;
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
        OnPropertyChanged(nameof(DoneBody));
        OnPropertyChanged(nameof(TryItBody));
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

/// <summary>
///     One detected model server, or the Codex CLI when <see cref="Url" /> is null. Selecting a
///     reachable server makes it the configured local server.
/// </summary>
public sealed partial class DetectionRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse), nameof(IsInUse), nameof(NeedsAttention), nameof(StatusSymbol))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsBusy),
        nameof(IsReachable),
        nameof(IsSatisfied),
        nameof(NeedsAttention),
        nameof(StatusSymbol),
        nameof(CanUse),
        nameof(IsInUse)
    )]
    private ProviderStatus? _status;

    [ObservableProperty]
    private string _summary;

    public DetectionRow(string title, string? url, string summary)
    {
        Title = title;
        Url = url;
        _summary = summary;
    }

    public string Title { get; }
    public string? Url { get; }

    public bool IsServer => Url is not null;
    public bool IsBusy => Status is null;
    public bool IsReachable => Status is { } status && ModelProbeService.IsReachable(status);

    public bool IsSatisfied => IsServer ? IsReachable : Status?.Kind == ProviderStatusKind.Ready;

    /// <summary>A server that is not running is neutral unless it is the configured one.</summary>
    public bool NeedsAttention =>
        Status is { } status
        && (IsServer
            ? !IsReachable && IsSelected
            : status.Kind is ProviderStatusKind.NotSignedIn or ProviderStatusKind.Unreachable or ProviderStatusKind.Incompatible);

    public bool CanUse => IsServer && IsReachable && !IsSelected;
    public bool IsInUse => IsServer && IsReachable && IsSelected;

    public Symbol StatusSymbol =>
        Status switch
        {
            null => Symbol.ArrowSync,
            _ when IsSatisfied => Symbol.CheckmarkCircle,
            _ when NeedsAttention => Symbol.Warning,
            _ => Symbol.DismissCircle,
        };

    public void Apply(ProviderStatus status, string summary)
    {
        Status = status;
        Summary = summary;
    }
}
