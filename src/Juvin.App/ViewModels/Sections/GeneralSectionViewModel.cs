using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.App.Services;
using Juvin.Core.Interfaces;
using Juvin.Core.Models;
using Juvin.Platform;

namespace Juvin.App.ViewModels.Sections;

public sealed partial class GeneralSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ILoginItem _loginItem;
    private bool _applyingSettings;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _launchAtLogin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeLaunchAtLogin))]
    private bool _isChangingLaunchAtLogin;

    [ObservableProperty]
    private bool _launchAtLoginFailed;

    [ObservableProperty]
    private string _senderName = "";

    [ObservableProperty]
    private bool _isTrayAvailable;

    public GeneralSectionViewModel(ISettingsService settings, TrayIconService tray, ILoginItem loginItem)
    {
        _settings = settings;
        _loginItem = loginItem;
        IsTrayAvailable = tray.IsTrayAvailable;
        tray.AvailabilityChanged += (_, _) => IsTrayAvailable = tray.IsTrayAvailable;
        IsLoginItemSupported = loginItem.IsSupported;
        ApplySettings(settings.Current);
        settings.SettingsChanged += ApplySettings;
    }

    public bool IsLoginItemSupported { get; }

    public bool CanChangeLaunchAtLogin => IsLoginItemSupported && !IsChangingLaunchAtLogin;

    partial void OnCloseToTrayChanged(bool value)
    {
        if (!_applyingSettings)
        {
            Save(s => s with { CloseToTray = value });
        }
    }

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (!_applyingSettings)
        {
            _ = ChangeLaunchAtLoginAsync(value);
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

    // The switch is disabled until the OS answers, so two changes never race.
    private async Task ChangeLaunchAtLoginAsync(bool enable)
    {
        IsChangingLaunchAtLogin = true;
        LaunchAtLoginFailed = false;
        try
        {
            await SetLoginItemAsync(enable);
            if (!Save(s => s with { LaunchAtLogin = enable }))
            {
                // The entry must match the saved setting, which startup refreshes it from.
                LaunchAtLoginFailed = true;
                await SetLoginItemAsync(!enable);
            }
        }
        catch (LoginItemException ex)
        {
            Trace.WriteLine($"[GeneralSection] Could not change the login item: {ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
            LaunchAtLoginFailed = true;
            ApplySettings(_settings.Current);
        }
        finally
        {
            IsChangingLaunchAtLogin = false;
        }
    }

    private Task SetLoginItemAsync(bool enable) =>
        enable ? _loginItem.EnableAsync(CancellationToken.None) : _loginItem.DisableAsync(CancellationToken.None);

    private bool Save(Func<AppSettings, AppSettings> update)
    {
        try
        {
            _settings.Update(update);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[GeneralSection] Could not save settings: {ex.GetType().Name}");
            ApplySettings(_settings.Current);
            return false;
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _applyingSettings = true;
        try
        {
            CloseToTray = settings.CloseToTray;
            LaunchAtLogin = settings.LaunchAtLogin;
            SenderName = settings.SenderName ?? "";
        }
        finally
        {
            _applyingSettings = false;
        }
    }
}
