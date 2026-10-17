using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Juvin.Core.Localization;
using Juvin.Platform;

namespace Juvin.App.Services;

/// <summary>
///     The tray icon with Open and Exit. Avalonia's tray icon does nothing on desktops without a
///     tray host, so <see cref="IsTrayAvailable" /> also requires the platform probe to find one.
///     A tray host can start after the app, so <see cref="RefreshAsync" /> probes again.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string IconFileName = "juvin-mark-color-32.png";

    private readonly Localizer _localizer;
    private readonly TrayHostAvailability _host;
    private TrayIcon? _trayIcon;
    private bool _iconShown;

    public TrayIconService(Localizer localizer, ITrayHostProbe probe)
    {
        _localizer = localizer;
        _host = new TrayHostAvailability(probe);
        _host.Changed += (_, _) => AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ExitRequested;

    /// <summary>Raised on the thread that called <see cref="RefreshAsync" /> when the answer changes.</summary>
    public event EventHandler? AvailabilityChanged;

    public bool IsTrayAvailable => _iconShown && _host.IsAvailable;

    public void Initialize()
    {
        if (_trayIcon is not null || Application.Current is not { } app)
        {
            return;
        }

        _host.ProbeBlocking();
        try
        {
            _trayIcon = new TrayIcon
            {
                ToolTipText = _localizer["App.Name"],
                Icon = LoadIcon(),
                Menu = BuildMenu(),
                IsVisible = true,
            };
            _trayIcon.Clicked += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
            TrayIcon.SetIcons(app, [_trayIcon]);
            _iconShown = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TrayIconService] Tray init failed: {ex.Message}");
        }

        Trace.WriteLine($"[TrayIconService] Tray available: {IsTrayAvailable}");
    }

    /// <summary>
    ///     Probes for a tray host again and returns <see cref="IsTrayAvailable" />; an unknown answer keeps
    ///     the last one. Stays false when the tray icon itself could not be created.
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        if (!_iconShown)
        {
            return false;
        }

        await _host.RefreshAsync();
        return IsTrayAvailable;
    }

    public void Dispose()
    {
        if (_trayIcon is null)
        {
            return;
        }

        if (Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, null);
        }

        _trayIcon.Dispose();
        _trayIcon = null;
        _iconShown = false;
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            var file = Path.Join(AppContext.BaseDirectory, "Assets", IconFileName);
            return File.Exists(file)
                ? new WindowIcon(file)
                : new WindowIcon(AssetLoader.Open(new Uri($"avares://juvin/Assets/{IconFileName}")));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TrayIconService] Icon load failed: {ex.Message}");
            return null;
        }
    }

    private NativeMenu BuildMenu()
    {
        var open = new NativeMenuItem(_localizer["Tray.OpenJuvin"]);
        open.Click += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);

        var exit = new NativeMenuItem(_localizer["Tray.Exit"]);
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        return [open, new NativeMenuItemSeparator(), exit];
    }
}
