// ReSharper disable UnusedParameterInPartialMethod -- the property change hooks have their signature fixed by the source generator
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.App.Services;
using Juvin.Core.Connector;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;

namespace Juvin.App.ViewModels;

public enum RewritePanelState
{
    // ReSharper disable once UnusedMember.Global -- the default value, which the panel holds before its first capture
    Idle,
    Running,
    Done,
    Failed,
}

/// <summary>
///     The rewrite panel: shows each capture idle until the user picks an action, then lets them
///     switch action, ask follow-ups, compare, and Apply, Copy, or Recapture. Traces carry lengths,
///     counts, action ids, and error codes only, never the draft, thread, prompt, or result.
/// </summary>
/// <remarks>
///     Members are called on the UI thread. Live previews arrive on provider threads and go
///     through the <c>post</c> marshaller given to the constructor.
/// </remarks>
public sealed partial class RewritePanelViewModel : ObservableObject
{
    private const string DraftChangedCode = "draft-changed";

    private static readonly TimeSpan s_copiedHintDuration = TimeSpan.FromSeconds(2);

    private readonly RewritePromptBuilder? _builder;
    private readonly IHistoryService _history;
    private readonly Localizer _localizer;
    private readonly Action<Action> _post;
    private readonly ProviderSelector _providers;
    private readonly ISettingsService _settings;

    private CapturedContext? _capture;
    private IReadOnlyList<DiffSegment>? _changes;
    private int _copiedHints;
    private int _generation;

    // Marked applied when Apply succeeds.
    private string? _historyEntryId;
    private RewriteAction? _lastAction;
    private string? _lastInstruction;
    private CancellationTokenSource? _recapture;
    private string? _recaptureEditorId;
    private CancellationTokenSource? _run;
    // The chip last picked for this capture, which Recapture runs again; null until one is picked.
    private RewriteAction? _selectedAction;
    private RewriteSession? _session;

    // Started the session; follow-ups inherit whether its results can be applied.
    private RewriteAction? _sessionAction;
    private IRewriteTarget? _target;

    [ObservableProperty]
    private string _adapterLabel = "";

    [ObservableProperty]
    private string _completenessLabel = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private string _followUpText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDraftChanged;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string _messageCountLabel = "";

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private string? _olderMessagesHint;

    [ObservableProperty]
    private string _providerLabel = "";

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private bool _showChanges;

    [ObservableProperty]
    private bool _showCopied;

    [ObservableProperty]
    private RewritePanelState _state;

    [ObservableProperty]
    private string? _subject;

    [ObservableProperty]
    private string _workingText = "";

    /// <param name="providers">Builds the provider for each new session from the current settings.</param>
    /// <param name="settings">The settings each new session is built from.</param>
    /// <param name="history">Where completed rewrites are kept when history is turned on.</param>
    /// <param name="localizer">The string table for labels and messages.</param>
    /// <param name="post">Runs an action on the UI thread; tests pass one that runs it inline.</param>
    /// <param name="builder">Builds each session's first prompt; null uses the default budget.</param>
    public RewritePanelViewModel(
        ProviderSelector providers,
        ISettingsService settings,
        IHistoryService history,
        Localizer localizer,
        Action<Action> post,
        RewritePromptBuilder? builder = null
    )
    {
        _providers = providers;
        _builder = builder;
        _settings = settings;
        _history = history;
        _localizer = localizer;
        _post = post;
        RebuildChips(settings.Current);
        settings.SettingsChanged += changed => _post(() => RebuildChips(changed));
    }

    public ObservableCollection<PanelActionChip> Actions { get; } = [];

    public ObservableCollection<ContextMessageItem> ContextMessages { get; } = [];

    /// <summary>Writes text to the clipboard; set by the window, which owns the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public bool HasCapture => _capture is not null;
    public bool HasSubject => !string.IsNullOrWhiteSpace(Subject);
    public bool HasContextMessages => ContextMessages.Count > 0;
    public bool IsIdle => State == RewritePanelState.Idle && HasCapture;
    public bool IsRunning => State == RewritePanelState.Running;
    public bool IsWorking => IsRunning && !IsStreaming;
    public bool HasError => State == RewritePanelState.Failed;
    public bool HasResult => ResultText.Length > 0;
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public bool HasOlderMessagesHint => OlderMessagesHint is not null;
    public bool CanShowChanges => State == RewritePanelState.Done && IsSessionApplicable;
    public bool IsChangesVisible => ShowChanges && CanShowChanges;
    public bool IsTextVisible => HasResult && !IsChangesVisible;

    /// <summary>The captured draft against the result; empty unless the Changes view is showing.</summary>
    public IReadOnlyList<DiffSegment> Changes =>
        IsChangesVisible && _capture is not null ? _changes ??= TextDiff.Compute(_capture.Context.Draft, ResultText) : [];

    public bool CanApply =>
        State == RewritePanelState.Done
        && IsSessionApplicable
        && _capture is not null
        && _target is not null
        && !IsDraftChanged
        && !IsBusy;

    public bool CanCopy => HasResult;

    /// <summary>"What am I missing?" returns advice, never a draft, so neither it nor its follow-ups can replace the selection.</summary>
    private bool IsSessionApplicable => _sessionAction is { IsMissing: false };
    public bool CanRecapture => _target is not null && !IsBusy;
    public bool CanSendFollowUp => _session is not null && !IsRunning && RewriteActionCatalog.IsValidInstruction(FollowUpText);

    /// <summary>Raised when the panel should hide: Close, Escape, or a successful Apply.</summary>
    public event Action? HideRequested;

    /// <summary>Raised when a successful capture, including a Recapture, has been loaded.</summary>
    public event Action? CaptureLoaded;

    /// <summary>
    ///     Shows a new capture, or the extension's error, replacing whatever the panel held; nothing runs
    ///     until an action is picked. A null <paramref name="target" /> (no browser connected) disables Recapture.
    /// </summary>
    public Task LoadAsync(IRewriteTarget? target, ContextResult result)
    {
        _target = target;
        _recaptureEditorId = null;
        return LoadCaptureAsync(result, null);
    }

    /// <summary>
    ///     Shows a result the action menu finished but the browser disconnected before it could take. There is
    ///     no target, so the user can copy it and ask follow-ups but not Apply or Recapture.
    /// </summary>
    public void ShowResult(ContextResult capture, RewriteSession session, RewriteAction action, RewriteResult result)
    {
        Supersede();
        _target = null;
        _recaptureEditorId = null;
        _capture = capture.Capture;
        _changes = null;
        _session = session;
        _sessionAction = action;
        _lastAction = action;
        _lastInstruction = null;
        _historyEntryId = null;
        FollowUpText = "";
        IsDraftChanged = false;
        ShowChanges = false;
        ErrorMessage = null;
        CanRetry = false;
        SelectChip(action.IsCustom ? null : action);
        ShowContext(_capture, result.Provenance);
        ProviderLabel = DescribeProvider(session.Provider, result.Provenance.ModelId);
        ResultText = result.Text;
        Notice = _localizer["Panel.Error.Disconnected"];
        State = RewritePanelState.Done;
        Trace.WriteLine($"[RewritePanel] Showing a {action.Id} result of {result.Text.Length} chars the browser did not take.");
    }

    partial void OnStateChanged(RewritePanelState value) => Refresh();

    partial void OnResultTextChanged(string value)
    {
        _changes = null;
        Refresh();
    }

    partial void OnIsBusyChanged(bool value) => Refresh();

    partial void OnIsDraftChangedChanged(bool value) => Refresh();

    partial void OnIsStreamingChanged(bool value) => Refresh();

    partial void OnNoticeChanged(string? value) => Refresh();

    partial void OnShowChangesChanged(bool value) => Refresh();

    partial void OnFollowUpTextChanged(string value) => Refresh();

    partial void OnSubjectChanged(string? value) => OnPropertyChanged(nameof(HasSubject));

    partial void OnOlderMessagesHintChanged(string? value) => OnPropertyChanged(nameof(HasOlderMessagesHint));

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SelectActionAsync(PanelActionChip? chip) =>
        chip is null || _capture is null ? Task.CompletedTask : StartActionAsync(chip.Action);

    [RelayCommand(CanExecute = nameof(CanSendFollowUp), AllowConcurrentExecutions = true)]
    private Task SendFollowUpAsync()
    {
        var instruction = FollowUpText.Trim();
        FollowUpText = "";
        return RunAsync(RewriteAction.Custom, instruction);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RetryAsync()
    {
        if (_capture is null || _lastAction is not { } action)
        {
            return Task.CompletedTask;
        }

        // A failed first turn rebuilds the session so a model corrected in Settings is picked up.
        return _session is null || _session.History.Count == 0
            ? StartActionAsync(action, _lastInstruction)
            : RunAsync(action, _lastInstruction);
    }

    [RelayCommand]
    private void Cancel() => _run?.Cancel();

    [RelayCommand]
    private void ShowRewriteView() => ShowChanges = false;

    [RelayCommand]
    private void ShowChangesView() => ShowChanges = true;

    [RelayCommand]
    private void Close()
    {
        Supersede();
        if (State == RewritePanelState.Running)
        {
            Fail(_localizer["Panel.Cancelled"], canRetry: true);
        }

        HideRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var capture = _capture!;
        var target = _target!;
        var text = ResultText;
        var generation = _generation;
        var historyEntryId = _historyEntryId;
        Notice = null;
        IsBusy = true;
        ConnectorError? error;
        try
        {
            error = await target.ApplyAsync(capture, text, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }

        if (error is null && historyEntryId is not null)
        {
            WriteHistory(() => _history.MarkApplied(historyEntryId));
        }

        if (generation != _generation)
        {
            return;
        }

        if (error is null)
        {
            Trace.WriteLine($"[RewritePanel] Applied {text.Length} chars.");
            HideRequested?.Invoke();
            return;
        }

        Trace.WriteLine($"[RewritePanel] Apply refused ({ConnectorTrace.Code(error.Code)}).");
        if (error.Code == DraftChangedCode)
        {
            IsDraftChanged = true;
            Notice = _localizer["Panel.DraftChanged"];
        }
        else
        {
            Notice = ErrorText(error.Code);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync()
    {
        if (CopyToClipboard is null)
        {
            return;
        }

        await CopyToClipboard(ResultText);
        Trace.WriteLine($"[RewritePanel] Copied {ResultText.Length} chars.");
        _ = ShowCopiedHintAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRecapture))]
    private async Task RecaptureAsync()
    {
        var target = _target!;
        Supersede();
        var recapture = new CancellationTokenSource();
        _recapture = recapture;
        var generation = _generation;
        IsBusy = true;
        ContextResult result;
        try
        {
            result = await target.RecaptureAsync(_recaptureEditorId, recapture.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsBusy = false;
            if (_recapture == recapture)
            {
                _recapture = null;
            }

            recapture.Dispose();
        }

        // Superseded by Close, a new capture, or another action.
        if (generation != _generation)
        {
            Trace.WriteLine("[RewritePanel] Discarded a superseded recapture.");
            return;
        }

        if (result.Capture is null && _capture is not null)
        {
            // Keep the capture and result so the user can still copy the rewrite.
            Trace.WriteLine($"[RewritePanel] Recapture declined ({ConnectorTrace.Code(result.Error?.Code)}); keeping the current capture.");
            if (State == RewritePanelState.Running)
            {
                Fail(_localizer["Panel.Cancelled"], canRetry: true);
            }

            Notice = ErrorText(result.Error?.Code);
            return;
        }

        // The picked action may have been edited or removed since; an unknown one loads the capture idle.
        var rerun = _selectedAction is null ? null : RewriteActionCatalog.Find(_settings.Current, _selectedAction.Id);
        // Not awaited, so Recapture is available again while the model runs; LoadCaptureAsync handles its own failures.
        _ = LoadCaptureAsync(result, rerun is { IsCustom: false } ? rerun : null);
    }

    /// <summary>Loads a capture and runs <paramref name="action" /> over it, or with none leaves the panel idle.</summary>
    private Task LoadCaptureAsync(ContextResult result, RewriteAction? action)
    {
        Supersede();
        _capture = result.Capture;
        _recaptureEditorId = result.Capture?.EditorId ?? _recaptureEditorId;
        _session = null;
        _sessionAction = null;
        ResultText = "";
        FollowUpText = "";
        Notice = null;
        IsDraftChanged = false;
        ShowChanges = false;
        SelectChip(action);
        ShowContext(result.Capture, null);
        ProviderLabel = DescribeProvider(null, null);

        if (result.Capture is null)
        {
            Trace.WriteLine($"[RewritePanel] Capture declined ({ConnectorTrace.Code(result.Error?.Code)}).");
            Fail(ErrorText(result.Error?.Code), canRetry: false);
            return Task.CompletedTask;
        }

        CaptureLoaded?.Invoke();
        if (action is not null)
        {
            return StartActionAsync(action);
        }

        _lastAction = null;
        _lastInstruction = null;
        ErrorMessage = null;
        CanRetry = false;
        State = RewritePanelState.Idle;
        return Task.CompletedTask;
    }

    /// <summary>A different action is a fresh first turn, so it gets a new session over the same capture.</summary>
    /// <remarks>
    ///     Custom only comes from retrying a follow-up whose first turn failed; it keeps the chip,
    ///     and whether results are applicable, of the action that started the conversation.
    /// </remarks>
    private Task StartActionAsync(RewriteAction action, string? instruction = null)
    {
        Supersede();
        if (!action.IsCustom)
        {
            SelectChip(action);
            _sessionAction = action;
        }

        _session = null;
        _lastAction = action;
        _lastInstruction = instruction;
        try
        {
            _session = new RewriteSession(_providers, _settings.Current, _capture!.Context, _builder);
        }
        catch (ProviderException ex)
        {
            Trace.WriteLine($"[RewritePanel] No provider for {action.Id} ({ex.Kind}).");
            ResultText = "";
            Fail(ex.Message, canRetry: true);
            return Task.CompletedTask;
        }

        ProviderLabel = DescribeProvider(_session.Provider, null);
        return RunAsync(action, instruction);
    }

    private async Task RunAsync(RewriteAction action, string? instruction)
    {
        var session = _session!;
        var capture = _capture!;
        Supersede();
        var run = new CancellationTokenSource();
        _run = run;
        var generation = _generation;
        _lastAction = action;
        _lastInstruction = instruction;
        _historyEntryId = null;
        ResultText = "";
        ErrorMessage = null;
        // A known-stale capture stays stale until a new capture arrives, so Apply is not offered again.
        if (!IsDraftChanged)
        {
            Notice = null;
        }

        session.CaptureProvenance = _settings.Current.ProvenanceCaptureEnabled;
        IsStreaming = session.Provider.SupportsStreaming;
        WorkingText = _localizer.Format("Panel.Working", session.Provider.DisplayName);
        State = RewritePanelState.Running;
        Trace.WriteLine(
            $"[RewritePanel] Running {action.Id}, turn {session.History.Count / 2 + 1}, "
                + $"{capture.Context.Draft.Length} draft chars, {capture.Context.Thread.Count} messages."
        );

        var preview = IsStreaming
            ? new InlineProgress<string>(text =>
                _post(() =>
                {
                    if (generation == _generation && State == RewritePanelState.Running)
                    {
                        ResultText = text;
                    }
                })
            )
            : null;

        try
        {
            var result = await session.RunAsync(action, instruction, preview, run.Token);
            if (generation != _generation)
            {
                return;
            }

            ResultText = result.Text;
            State = RewritePanelState.Done;
            ShowContext(capture, result.Provenance);
            ProviderLabel = DescribeProvider(session.Provider, result.Provenance.ModelId);
            Trace.WriteLine(
                $"[RewritePanel] {action.Id} done, {result.Text.Length} chars in {(int)result.Provenance.Duration.TotalMilliseconds} ms."
            );
            RecordHistory(action, capture, result);
        }
        catch (ProviderException ex) when (generation == _generation)
        {
            Trace.WriteLine($"[RewritePanel] {action.Id} failed ({ex.Kind}).");
            Fail(ex.Kind == ProviderFailureKind.Cancelled ? _localizer["Panel.Cancelled"] : ex.Message, canRetry: true);
        }
        catch (OperationCanceledException) when (generation == _generation)
        {
            Trace.WriteLine($"[RewritePanel] {action.Id} cancelled.");
            Fail(_localizer["Panel.Cancelled"], canRetry: true);
        }
        catch (Exception ex) when (generation != _generation && ex is ProviderException or OperationCanceledException)
        {
            // Superseded by a newer run, capture, or Close; that one owns the panel now.
        }
        finally
        {
            if (_run == run)
            {
                _run = null;
            }

            run.Dispose();
        }
    }

    /// <summary>Keeps the result in history when it is turned on. A failed write never hides the result.</summary>
    private void RecordHistory(RewriteAction action, CapturedContext capture, RewriteResult result)
    {
        var settings = _settings.Current;
        if (!settings.HistoryEnabled)
        {
            return;
        }

        var entry = RewriteHistory.CreateEntry(action, capture, result, settings, _localizer);
        WriteHistory(() =>
        {
            _history.Add(entry);
            _historyEntryId = entry.Id;
            Trace.WriteLine($"[RewritePanel] Kept history entry {entry.Id}.");
            _history.Prune(settings.HistoryRetentionDays, entry.CreatedAt);
        });
    }

    private static void WriteHistory(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[RewritePanel] Could not save history: {ex.GetType().Name}");
        }
    }

    /// <summary>Cancels the running rewrite or recapture and makes any late result or preview from it stale.</summary>
    private void Supersede()
    {
        _generation++;
        _run?.Cancel();
        _run = null;
        _recapture?.Cancel();
        _recapture = null;
    }

    private void Fail(string message, bool canRetry)
    {
        ErrorMessage = message;
        CanRetry = canRetry;
        State = RewritePanelState.Failed;
    }

    private void SelectChip(RewriteAction? action)
    {
        _selectedAction = action;
        foreach (var chip in Actions)
        {
            chip.IsSelected = chip.Action.Id == action?.Id;
        }
    }

    /// <summary>
    ///     Lists the configured actions. The selection follows its action by id and is lost only when
    ///     that action was removed; a running session keeps the action it started with.
    /// </summary>
    private void RebuildChips(AppSettings settings)
    {
        var selectedId = Actions.FirstOrDefault(chip => chip.IsSelected)?.Action.Id;
        Actions.Clear();
        foreach (var action in RewriteActionCatalog.Resolve(settings).Where(action => !action.IsCustom))
        {
            Actions.Add(new PanelActionChip(action, action.DisplayName(_localizer)) { IsSelected = action.Id == selectedId });
        }
    }

    private void ShowContext(CapturedContext? capture, RewriteProvenance? provenance)
    {
        ContextMessages.Clear();
        OlderMessagesHint = null;
        if (capture is null)
        {
            AdapterLabel = "";
            MessageCountLabel = "";
            CompletenessLabel = "";
            Subject = null;
            Refresh();
            return;
        }

        var context = capture.Context;
        AdapterLabel = AdapterLabels.Describe(capture.Adapter, _localizer);

        var count = provenance?.ThreadMessageCount ?? context.Thread.Count;
        MessageCountLabel = CaptureLabels.MessageCount(count, _localizer);
        CompletenessLabel = CaptureLabels.Completeness(provenance?.Completeness ?? context.Completeness, context.CollapsedCount, _localizer);
        Subject = context.Subject;
        // Once a result is in, list only the messages the prompt builder kept; it drops the oldest.
        var shown = provenance is null ? context.Thread.Count : Math.Min(provenance.ThreadMessageCount, context.Thread.Count);
        var leftOut = context.Thread.Count - shown;
        if (leftOut > 0)
        {
            OlderMessagesHint = leftOut == 1
                ? _localizer["Panel.OlderMessagesLeftOutOne"]
                : _localizer.Format("Panel.OlderMessagesLeftOut", leftOut);
        }

        foreach (var (message, index) in context.Thread.Skip(leftOut).Select((message, index) => (message, index)))
        {
            var body = message.Body;
            // The newest message shows only the part the builder kept, so the list matches what the model saw.
            if (index == shown - 1 && provenance?.NewestMessageBodyLength is { } kept && kept < body.Trim().Length)
            {
                body = body.Trim()[..kept] + "\n\n" + _localizer["Panel.NewestMessageCutShort"];
            }

            ContextMessages.Add(
                new ContextMessageItem(
                    message.From,
                    message.Sent?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    body
                )
            );
        }

        Refresh();
    }

    private string DescribeProvider(IModelProvider? provider, string? modelId) =>
        CaptureLabels.Provider(provider, modelId, _settings.Current, _localizer);

    private string ErrorText(string? code) => _localizer[ErrorKeyFor(code)];

    /// <summary>The string table key for a connector error code; unknown codes get a generic message.</summary>
    public static string ErrorKeyFor(string? code) =>
        code switch
        {
            "no-active-tab" => "Panel.Error.NoActiveTab",
            "no-adapter" => "Panel.Error.NoAdapter",
            "no-editor" => "Panel.Error.NoEditor",
            "no-selection" => "Panel.Error.NoSelection",
            "page-not-ready" => "Panel.Error.PageNotReady",
            "editor-gone" => "Panel.Error.EditorGone",
            ConnectorService.BrowserNotFocusedCode => "Panel.Error.BrowserNotFocused",
            ConnectorService.NoBrowserCode => "Panel.Error.NoBrowser",
            ConnectorErrorCodes.Disconnected => "Panel.Error.Disconnected",
            ConnectorRewriteTarget.TimeoutCode => "Panel.Error.Timeout",
            ConnectorRewriteTarget.TooLargeCode => "Panel.Error.TooLarge",
            _ => "Panel.Error.Unknown",
        };

    private async Task ShowCopiedHintAsync()
    {
        var hint = ++_copiedHints;
        ShowCopied = true;
        await Task.Delay(s_copiedHintDuration);
        if (hint == _copiedHints)
        {
            ShowCopied = false;
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasCapture));
        OnPropertyChanged(nameof(HasContextMessages));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasNotice));
        OnPropertyChanged(nameof(CanShowChanges));
        OnPropertyChanged(nameof(IsChangesVisible));
        OnPropertyChanged(nameof(IsTextVisible));
        OnPropertyChanged(nameof(Changes));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanRecapture));
        OnPropertyChanged(nameof(CanSendFollowUp));
        ApplyCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        RecaptureCommand.NotifyCanExecuteChanged();
        SendFollowUpCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One action button in the panel.</summary>
public sealed partial class PanelActionChip : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public PanelActionChip(RewriteAction action, string title)
    {
        Action = action;
        Title = title;
    }

    public RewriteAction Action { get; }
    public string Title { get; }
}

/// <summary>A thread message as the Context list shows it. Displayed only, never traced.</summary>
public sealed record ContextMessageItem(string From, string? Sent, string Body)
{
    public bool HasSent => Sent is not null;
}
