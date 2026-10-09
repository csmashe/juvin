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
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string IconFileName = "juvin-mark-color-32.png";

    private readonly Localizer _localizer;
    private readonly ITrayHostProbe _probe;
    private TrayIcon? _trayIcon;

    public TrayIconService(Localizer localizer, ITrayHostProbe probe)
    {
        _localizer = localizer;
        _probe = probe;
    }

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ExitRequested;

    public bool IsTrayAvailable { get; private set; }

    public void Initialize()
    {
        if (_trayIcon is not null || Application.Current is not { } app)
        {
            return;
        }

        var hostPresent = _probe.IsTrayHostAvailable();
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
            IsTrayAvailable = hostPresent;
        }
        catch (Exception ex)
        {
            IsTrayAvailable = false;
            Trace.WriteLine($"[TrayIconService] Tray init failed: {ex.Message}");
        }

        Trace.WriteLine($"[TrayIconService] Tray available: {IsTrayAvailable}");
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
