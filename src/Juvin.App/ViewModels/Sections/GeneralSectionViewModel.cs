using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.App.Services;
using Juvin.Core.Interfaces;
using Juvin.Core.Models;

namespace Juvin.App.ViewModels.Sections;

public sealed partial class GeneralSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private bool _applyingSettings;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private string _senderName = "";

    public GeneralSectionViewModel(ISettingsService settings, TrayIconService tray)
    {
        _settings = settings;
        IsTrayAvailable = tray.IsTrayAvailable;
        ApplySettings(settings.Current);
        settings.SettingsChanged += ApplySettings;
    }

    public bool IsTrayAvailable { get; }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (!_applyingSettings)
        {
            Save(s => s with { CloseToTray = value });
        }
    }

    partial void OnSenderNameChanged(string value)
    {
        if (!_applyingSettings)
        {
            Save(s => s with { SenderName = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
        }
    }

    // The text box saves on focus loss; Enter commits through this.
    [RelayCommand]
    private void CommitSenderName(string? text) => SenderName = text ?? "";

    private void Save(Func<AppSettings, AppSettings> update)
    {
        try
        {
            _settings.Update(update);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[GeneralSection] Could not save settings: {ex.GetType().Name}");
            ApplySettings(_settings.Current);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _applyingSettings = true;
        try
        {
            CloseToTray = settings.CloseToTray;
            SenderName = settings.SenderName ?? "";
        }
        finally
        {
            _applyingSettings = false;
        }
    }
}
