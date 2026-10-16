using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.App.Services.CrashReporting;
using Juvin.Core;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Services;
using Juvin.Platform;

namespace Juvin.App.ViewModels.Sections;

/// <summary>
///     History, provenance, and the data Juvin keeps. A setting that could not be saved snaps back to
///     the saved value; a history or delete failure shows its error in the Storage card.
/// </summary>
public sealed partial class PrivacyAndDataSectionViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly Localizer _localizer;
    private readonly ILoginItem _loginItem;
    private readonly ISettingsService _settings;
    private bool _applyingSettings;

    [ObservableProperty]
    private bool _crashReportingEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _historyEnabled;

    [ObservableProperty]
    private bool _isConfirmingClear;

    [ObservableProperty]
    private bool _isConfirmingDeleteAll;

    [ObservableProperty]
    private bool _provenanceCaptureEnabled;

    [ObservableProperty]
    private RetentionOption? _selectedRetention;

    public PrivacyAndDataSectionViewModel(
        ISettingsService settings,
        IHistoryService history,
        Localizer localizer,
        ILoginItem loginItem,
        ICrashReporter crashReporter
    )
        : this(settings, history, localizer, loginItem, crashReporter, JuvinEnvironment.DataPath)
    {
    }

    /// <param name="settings">The settings the toggles read and save.</param>
    /// <param name="history">The history the Storage card counts, exports, and clears.</param>
    /// <param name="localizer">The string table for labels and messages.</param>
    /// <param name="loginItem">The OS login entry Delete all data removes along with the setting.</param>
    /// <param name="crashReporter">Whether this build can send crash reports at all.</param>
    /// <param name="dataPath">The folder whose secrets Delete all data removes; tests pass a temporary one.</param>
    public PrivacyAndDataSectionViewModel(
        ISettingsService settings,
        IHistoryService history,
        Localizer localizer,
        ILoginItem loginItem,
        ICrashReporter crashReporter,
        string dataPath
    )
    {
        _settings = settings;
        _history = history;
        _localizer = localizer;
        _loginItem = loginItem;
        DataFolder = dataPath;
        IsCrashReportingAvailable = crashReporter.IsAvailable;
        RetentionOptions =
        [
            new RetentionOption(7, localizer["PrivacyAndData.Retention.Week"]),
            new RetentionOption(30, localizer["PrivacyAndData.Retention.Month"]),
            new RetentionOption(90, localizer["PrivacyAndData.Retention.Quarter"]),
            new RetentionOption(365, localizer["PrivacyAndData.Retention.Year"]),
            new RetentionOption(0, localizer["PrivacyAndData.Retention.Forever"]),
        ];
        ApplySettings(settings.Current);
        settings.SettingsChanged += ApplySettings;
        history.Changed += OnHistoryChanged;
    }

    public string DataFolder { get; }
    public bool IsCrashReportingAvailable { get; }
    public IReadOnlyList<RetentionOption> RetentionOptions { get; }
    public bool HasError => ErrorMessage is not null;
    public bool HasEntries => _history.Entries.Count > 0;

    public string HistorySummary =>
        !_history.IsAvailable ? _localizer["PrivacyAndData.HistoryUnavailable"]
        : _history.Entries.Count == 1 ? _localizer["PrivacyAndData.HistoryCountOne"]
        : _localizer.Format("PrivacyAndData.HistoryCount", _history.Entries.Count);

    public string ExportMarkdown() => HistoryExport.ToMarkdown(_history.Entries);

    public string ExportJson() => HistoryExport.ToJson(_history.Entries);

    public void ReportExportFailure(Exception ex)
    {
        Trace.WriteLine($"[PrivacyAndDataSection] Could not export history: {ex.GetType().Name}");
        ErrorMessage = _localizer.Format("PrivacyAndData.ExportFailed", ex.Message);
    }

    partial void OnHistoryEnabledChanged(bool value) => SaveSetting(s => s with { HistoryEnabled = value });

    partial void OnProvenanceCaptureEnabledChanged(bool value) => SaveSetting(s => s with { ProvenanceCaptureEnabled = value });

    partial void OnCrashReportingEnabledChanged(bool value) => SaveSetting(s => s with { CrashReportingEnabled = value });

    partial void OnSelectedRetentionChanged(RetentionOption? value)
    {
        if (_applyingSettings || value is null || !SaveSetting(s => s with { HistoryRetentionDays = value.Days }))
        {
            return;
        }

        WriteHistory(() => _history.Prune(value.Days, DateTimeOffset.UtcNow));
    }

    [RelayCommand]
    private void RequestClear()
    {
        IsConfirmingDeleteAll = false;
        IsConfirmingClear = true;
    }

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    [RelayCommand]
    private void ConfirmClear()
    {
        IsConfirmingClear = false;
        if (WriteHistory(_history.Clear))
        {
            Trace.WriteLine("[PrivacyAndDataSection] Cleared history.");
        }
    }

    [RelayCommand]
    private void RequestDeleteAll()
    {
        IsConfirmingClear = false;
        IsConfirmingDeleteAll = true;
    }

    [RelayCommand]
    private void CancelDeleteAll() => IsConfirmingDeleteAll = false;

    [RelayCommand]
    private async Task ConfirmDeleteAllAsync()
    {
        IsConfirmingDeleteAll = false;
        ErrorMessage = null;
        var removedLoginItem = false;
        try
        {
            // First, so a refused removal leaves the setting that still describes the entry.
            if (_settings.Current.LaunchAtLogin)
            {
                await _loginItem.DisableAsync(CancellationToken.None);
                removedLoginItem = true;
            }

            _history.Clear();
            JuvinEnvironment.DeleteSecretsIn(DataFolder);
            _settings.Reset();
            Trace.WriteLine("[PrivacyAndDataSection] Deleted all data.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LoginItemException)
        {
            Trace.WriteLine($"[PrivacyAndDataSection] Could not delete all data: {ex.GetType().Name}");
            ErrorMessage = _localizer.Format("PrivacyAndData.DeleteAllFailed", ex.Message);
            ApplySettings(_settings.Current);
            // Reset commits the defaults before its cleanup, so it can fail with the setting already off.
            if (removedLoginItem && _settings.Current.LaunchAtLogin)
            {
                await RestoreLoginItemAsync();
            }
        }
    }

    private async Task RestoreLoginItemAsync()
    {
        try
        {
            await _loginItem.EnableAsync(CancellationToken.None);
        }
        catch (LoginItemException ex)
        {
            Trace.WriteLine($"[PrivacyAndDataSection] Could not restore the login item: {ex.GetType().Name}");
        }
    }

    private bool SaveSetting(Func<AppSettings, AppSettings> update)
    {
        if (_applyingSettings)
        {
            return false;
        }

        ErrorMessage = null;
        try
        {
            _settings.Update(update);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[PrivacyAndDataSection] Could not save settings: {ex.GetType().Name}");
            ErrorMessage = _localizer.Format("PrivacyAndData.SaveFailed", ex.Message);
            ApplySettings(_settings.Current);
            return false;
        }
    }

    private bool WriteHistory(Action write)
    {
        ErrorMessage = null;
        try
        {
            write();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[PrivacyAndDataSection] Could not change history: {ex.GetType().Name}");
            ErrorMessage = _localizer.Format("PrivacyAndData.HistoryFailed", ex.Message);
            return false;
        }
    }

    private void OnHistoryChanged()
    {
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HistorySummary));
    }

    private void ApplySettings(AppSettings settings)
    {
        _applyingSettings = true;
        try
        {
            HistoryEnabled = settings.HistoryEnabled;
            ProvenanceCaptureEnabled = settings.ProvenanceCaptureEnabled;
            CrashReportingEnabled = settings.CrashReportingEnabled;
            // A hand-edited period that is not offered selects nothing; it still applies.
            SelectedRetention = RetentionOptions.FirstOrDefault(option =>
                option.Days == settings.HistoryRetentionDays || (option.Days == 0 && settings.HistoryRetentionDays < 0)
            );
        }
        finally
        {
            _applyingSettings = false;
        }
    }
}

/// <param name="Days">Zero keeps history until it is cleared.</param>
public sealed record RetentionOption(int Days, string Title);
