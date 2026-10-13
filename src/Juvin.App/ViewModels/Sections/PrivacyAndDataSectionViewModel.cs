using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.Core;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Services;

namespace Juvin.App.ViewModels.Sections;

/// <summary>
///     History, provenance, and the data Juvin keeps. A setting that could not be saved snaps back to
///     the saved value; a history or delete failure shows its error in the Storage card.
/// </summary>
public sealed partial class PrivacyAndDataSectionViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly Localizer _localizer;
    private readonly ISettingsService _settings;
    private bool _applyingSettings;

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

    public PrivacyAndDataSectionViewModel(ISettingsService settings, IHistoryService history, Localizer localizer)
        : this(settings, history, localizer, JuvinEnvironment.DataPath)
    {
    }

    /// <param name="settings">The settings the toggles read and save.</param>
    /// <param name="history">The history the Storage card counts, exports, and clears.</param>
    /// <param name="localizer">The string table for labels and messages.</param>
    /// <param name="dataPath">The folder whose secrets Delete all data removes; tests pass a temporary one.</param>
    public PrivacyAndDataSectionViewModel(
        ISettingsService settings,
        IHistoryService history,
        Localizer localizer,
        string dataPath
    )
    {
        _settings = settings;
        _history = history;
        _localizer = localizer;
        DataFolder = dataPath;
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
    private void ConfirmDeleteAll()
    {
        IsConfirmingDeleteAll = false;
        ErrorMessage = null;
        try
        {
            _history.Clear();
            JuvinEnvironment.DeleteSecretsIn(DataFolder);
            _settings.Reset();
            Trace.WriteLine("[PrivacyAndDataSection] Deleted all data.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[PrivacyAndDataSection] Could not delete all data: {ex.GetType().Name}");
            ErrorMessage = _localizer.Format("PrivacyAndData.DeleteAllFailed", ex.Message);
            ApplySettings(_settings.Current);
        }
    }

    private bool SaveSetting(Func<AppSettings, AppSettings> update)
    {
        if (_applyingSettings)
        {
            return false;
        }

        try
        {
            _settings.Update(update);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[PrivacyAndDataSection] Could not save settings: {ex.GetType().Name}");
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
