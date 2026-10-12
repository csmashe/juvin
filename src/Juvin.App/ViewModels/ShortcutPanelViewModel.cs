using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.App.Services;
using Juvin.Core.Hotkeys;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.ViewModels;

/// <summary>
///     The rewrite shortcut recorder, backend choice, listening status, and press test. Shared by the
///     Shortcuts section and the wizard's shortcut step.
/// </summary>
/// <remarks>
///     Members are called on the UI thread. Hotkey and settings events arrive on other threads and go
///     through the <c>post</c> marshaller given to the constructor.
/// </remarks>
public sealed partial class ShortcutPanelViewModel : ObservableObject, IDisposable
{
    private const string DesktopShortcutId = "desktop-shortcut";

    private static readonly TimeSpan s_detectedDuration = TimeSpan.FromSeconds(2);

    /// <summary>How long presses stay ignored after the recorded key is released, so a restarted backend never sees it.</summary>
    internal static TimeSpan ResumeGrace { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Resumes anyway when no release arrives, such as when focus moved while the key was held.</summary>
    internal static TimeSpan ReleaseCap { get; } = TimeSpan.FromSeconds(2);

    private readonly HotkeyService _hotkeys;
    private readonly bool _isMac;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Localizer _localizer;
    private readonly Action<Action> _post;
    private readonly ISettingsService _settings;

    private bool _applyingSettings;
    private bool _resumeAfterGrace;
    private bool _awaitingRelease;
    private int _resumeGeneration;
    private bool _suspendedByThisPanel;
    private int _detections;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDesktopShortcutMessage))]
    private string? _desktopShortcutMessage;

    [ObservableProperty]
    private bool _isDetected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecorderText))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecordHint))]
    private string? _recordHint;

    [ObservableProperty]
    private BackendOption? _selectedBackend;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecorderText))]
    private string _shortcutText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDesktopShortcut), nameof(ShowSignInNote))]
    private IDesktopShortcutInfo? _desktopShortcut;

    /// <param name="hotkeys">The service whose status and presses the panel shows.</param>
    /// <param name="settings">Holds the shortcut and backend choice.</param>
    /// <param name="localizer">Supplies the panel text.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="isMac">Whether keys are labelled and mapped for macOS; defaults to the current OS.</param>
    public ShortcutPanelViewModel(
        HotkeyService hotkeys,
        ISettingsService settings,
        Localizer localizer,
        Action<Action> post,
        bool? isMac = null
    )
    {
        _hotkeys = hotkeys;
        _settings = settings;
        _localizer = localizer;
        _post = post;
        _isMac = isMac ?? OperatingSystem.IsMacOS();
        RefreshStatus();
        ApplySettings(settings.Current);
        _hotkeys.StatusChanged += OnStatusChanged;
        _hotkeys.Pressed += OnPressed;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public ObservableCollection<BackendOption> BackendOptions { get; } = [];

    public string RecorderText => IsRecording ? _localizer["Shortcuts.Recorder.Prompt"] : ShortcutText;
    public bool HasRecordHint => !string.IsNullOrWhiteSpace(RecordHint);
    public bool ShowDesktopShortcut => DesktopShortcut is not null;
    public bool ShowSignInNote => DesktopShortcut?.RequiresSessionRestartToApply == true;
    public bool HasDesktopShortcutMessage => !string.IsNullOrWhiteSpace(DesktopShortcutMessage);

    private bool IsDisposed => _lifetime.IsCancellationRequested;

    public void Dispose()
    {
        IsRecording = false;
        _awaitingRelease = false;
        Resume();

        _hotkeys.StatusChanged -= OnStatusChanged;
        _hotkeys.Pressed -= OnPressed;
        _settings.SettingsChanged -= OnSettingsChanged;
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
    }

    public void BeginRecording()
    {
        _resumeGeneration++;
        _awaitingRelease = false;
        RecordHint = null;
        IsRecording = true;
    }

    public void CancelRecording()
    {
        IsRecording = false;
    }

    /// <summary>Records a key press while recording; returns whether the press was consumed.</summary>
    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        if (!IsRecording)
        {
            return false;
        }

        // ReSharper disable once ConvertIfStatementToSwitchStatement -- the checks mix keys, modifiers, and key-map lookups; a switch reads worse
        if (key == Key.Escape)
        {
            CancelRecording();
            return true;
        }

        // A bare Tab keeps moving focus, so the recorder never traps keyboard users.
        if (key == Key.Tab && modifiers == KeyModifiers.None)
        {
            return false;
        }

        if (ShortcutKeyMap.IsModifierKey(key))
        {
            RecordHint = _localizer["Shortcuts.Recorder.NeedsKey"];
            return true;
        }

        if (ShortcutKeyMap.KeyName(key) is not { } name)
        {
            RecordHint = _localizer["Shortcuts.Recorder.UnsupportedKey"];
            return true;
        }

        if (!KeyChord.TryCreate(ShortcutKeyMap.Modifiers(modifiers, _isMac), name, out var chord))
        {
            RecordHint = _localizer[_isMac ? "Shortcuts.Recorder.NeedsModifierMac" : "Shortcuts.Recorder.NeedsModifier"];
            return true;
        }

        try
        {
            _settings.Update(s => s with { RewriteShortcut = chord.ToString() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[ShortcutPanel] Could not save the shortcut: {ex.GetType().Name}");
            RecordHint = _localizer.Format("Shortcuts.SaveFailed", ex.Message);
            return true;
        }

        RecordHint = null;
        _resumeAfterGrace = true;
        IsRecording = false;
        _resumeAfterGrace = false;
        _awaitingRelease = true;
        _ = ResumeAfterAsync(ReleaseCap, force: true, ++_resumeGeneration);
        ApplySettings(_settings.Current);
        return true;
    }

    /// <summary>The recorded chord's key coming back up; a held key would otherwise autorepeat into the restarted backend.</summary>
    public void HandleKeyUp(Key key)
    {
        if (!_awaitingRelease || ShortcutKeyMap.IsModifierKey(key))
        {
            return;
        }

        _awaitingRelease = false;
        _ = ResumeAfterAsync(ResumeGrace, force: false, _resumeGeneration);
    }

    // Presses would otherwise start a capture while the user records the current shortcut.
    partial void OnIsRecordingChanged(bool value)
    {
        if (value)
        {
            _hotkeys.Suspended = true;
            _suspendedByThisPanel = true;
        }
        else if (!_resumeAfterGrace)
        {
            Resume();
        }
    }

    private void Resume()
    {
        if (!_suspendedByThisPanel)
        {
            return;
        }

        _suspendedByThisPanel = false;
        _hotkeys.Suspended = false;
    }

    private async Task ResumeAfterAsync(TimeSpan delay, bool force, int generation)
    {
        await Task.Delay(delay);
        _post(() =>
        {
            // This timer belongs to an earlier recording.
            if (generation != _resumeGeneration)
            {
                return;
            }

            // A disposed panel's timer must not resume while another panel (settings after the wizard) is recording.
            if (IsDisposed || IsRecording || (_awaitingRelease && !force))
            {
                return;
            }

            _awaitingRelease = false;
            Resume();
        });
    }

    partial void OnSelectedBackendChanged(BackendOption? value)
    {
        if (_applyingSettings || value is null)
        {
            return;
        }

        try
        {
            _settings.Update(s => s with { HotkeyBackend = value.Id });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[ShortcutPanel] Could not save the backend choice: {ex.GetType().Name}");
            ApplySettings(_settings.Current);
        }
    }

    [RelayCommand]
    private async Task RemoveDesktopShortcutAsync()
    {
        if (DesktopShortcut is not { } desktop || IsDisposed)
        {
            return;
        }

        DesktopShortcutMessage = null;
        try
        {
            await _hotkeys.RemoveDesktopShortcutAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShortcutPanel] Removing the desktop shortcut failed ({ex.GetType().Name}).");
            DesktopShortcutMessage = _localizer.Format("Shortcuts.DesktopShortcut.RemoveFailed", ex.Message);
            return;
        }

        if (!IsDisposed)
        {
            DesktopShortcutMessage = _localizer[
                desktop.RequiresSessionRestartToApply ? "Shortcuts.DesktopShortcut.RemovedUntilSignIn" : "Shortcuts.DesktopShortcut.Removed"
            ];
        }
    }

    private void OnStatusChanged() => _post(RefreshStatus);

    private void OnPressed() => _post(() => _ = ShowDetectedAsync());

    private void OnSettingsChanged(AppSettings settings) => _post(() => ApplySettings(settings));

    private async Task ShowDetectedAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var detection = ++_detections;
        IsDetected = true;
        await Task.Delay(s_detectedDuration);
        if (detection == _detections && !IsDisposed)
        {
            IsDetected = false;
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        if (IsDisposed)
        {
            return;
        }

        ShortcutText = KeyChord.TryParse(settings.RewriteShortcut, out var chord)
            ? chord.ToDisplayString(_isMac)
            : settings.RewriteShortcut;

        _applyingSettings = true;
        try
        {
            SelectedBackend = BackendOptions.FirstOrDefault(option => option.Id == settings.HotkeyBackend) ?? BackendOptions[0];
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void RefreshStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        var available = _hotkeys.AvailableBackends;
        string?[] ids = [null, .. available.Select(backend => backend.Id)];
        if (!BackendOptions.Select(option => option.Id).SequenceEqual(ids))
        {
            _applyingSettings = true;
            try
            {
                BackendOptions.Clear();
                BackendOptions.Add(new BackendOption(null, _localizer["Shortcuts.Backend.Automatic"]));
                foreach (var backend in available)
                {
                    BackendOptions.Add(new BackendOption(backend.Id, BackendName(backend.Id)));
                }
            }
            finally
            {
                _applyingSettings = false;
            }

            ApplySettings(_settings.Current);
        }

        var status = _hotkeys.Status;
        IsListening = status.Kind == HotkeyStatusKind.Active;
        StatusText = status.Kind switch
        {
            HotkeyStatusKind.Active => _localizer.Format("Shortcuts.Status.Active", BackendName(status.BackendId!)),
            HotkeyStatusKind.NoBackend => _localizer["Shortcuts.Status.NoBackend"],
            HotkeyStatusKind.InvalidShortcut => _localizer["Shortcuts.Status.InvalidShortcut"],
            HotkeyStatusKind.Failed when status.Message is { } message =>
                _localizer.Format("Shortcuts.Status.Failed", BackendName(status.BackendId!), message),
            HotkeyStatusKind.Failed => _localizer.Format("Shortcuts.Status.FailedNoReason", BackendName(status.BackendId!)),
            _ => _localizer["Shortcuts.Status.Stopped"],
        };

        var desktop = status is { Kind: HotkeyStatusKind.Active, BackendId: DesktopShortcutId }
            ? available.FirstOrDefault(backend => backend.Id == DesktopShortcutId) as IDesktopShortcutInfo
            : null;
        // The removal message must outlive the status change that hides the desktop block.
        if (desktop is not null && !ReferenceEquals(desktop, DesktopShortcut))
        {
            DesktopShortcutMessage = null;
        }

        DesktopShortcut = desktop;
    }

    private string BackendName(string id)
    {
        var key = $"Shortcuts.Backend.{id}";
        var name = _localizer[key];
        return name == key ? id : name;
    }
}

/// <summary>One choice in the backend list; a null <see cref="Id" /> is the automatic choice.</summary>
public sealed record BackendOption(string? Id, string Name);
