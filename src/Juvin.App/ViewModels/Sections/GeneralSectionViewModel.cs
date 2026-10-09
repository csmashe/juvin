using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
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
        if (_applyingSettings)
        {
            return;
        }

        try
        {
            _settings.Update(s => s with { CloseToTray = value });
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
        }
        finally
        {
            _applyingSettings = false;
        }
    }
}
