using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Juvin.App.Localization;
using Juvin.App.Services;
using Juvin.App.ViewModels;
using Juvin.App.Views;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.App;

public class App : Application
{
    private static readonly TimeSpan s_hotkeyStopTimeout = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _socketLifetime = new();
    private bool _exiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Start(desktop, Program.Services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        settings.Load();
        PruneHistory(services.GetRequiredService<IHistoryService>(), settings.Current.HistoryRetentionDays);

        // The language must be set before any view loads, because views read strings once.
        var localizer = services.GetRequiredService<Localizer>();
        localizer.SetLanguage(settings.Current.UiLanguage);
        StrExtension.Localizer = localizer;

        // The tray goes first so the General section sees whether a tray host exists.
        var tray = services.GetRequiredService<TrayIconService>();
        tray.Initialize();

        var main = services.GetRequiredService<MainWindow>();
        desktop.MainWindow = main;
        // The rewrite panel stays open while hidden, so it must not keep the app alive.
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        main.Closing += (_, e) =>
        {
            if (_exiting || e.CloseReason != WindowCloseReason.WindowClosing)
            {
                return;
            }

            if (!settings.Current.CloseToTray || !tray.IsTrayAvailable)
            {
                return;
            }

            e.Cancel = true;
            main.Hide();
        };

        tray.ShowSettingsRequested += (_, _) => ShowMainWindow(main);
        tray.ExitRequested += (_, _) =>
        {
            _exiting = true;
            desktop.Shutdown();
        };

        var commands = services.GetRequiredService<ControlCommandHandler>();
        commands.ShowRequested += () => Dispatcher.UIThread.Post(() => ShowMainWindow(main));
        _ = StartControlSocketAsync(services.GetRequiredService<ILocalSocketServer>(), commands);

        // The panel subscribes before the connector starts so no capture goes unanswered.
        var panel = services.GetRequiredService<RewritePanelService>();
        panel.Start();
        var connector = services.GetRequiredService<ConnectorService>();
        _ = StartConnectorAsync(connector);

        // A desktop environment's own shortcut runs `juvin --trigger`, which arrives as a press here.
        var hotkeys = services.GetRequiredService<HotkeyService>();
        commands.TriggerRequested += hotkeys.HandleTrigger;
        _ = StartHotkeysAsync(hotkeys);

        desktop.Exit += (_, _) =>
        {
            _exiting = true;
            panel.Stop();
            // Bounded: a backend may be mid-way through a desktop command, and shutdown must not hang on it.
            if (!hotkeys.StopAsync().Wait(s_hotkeyStopTimeout))
            {
                Trace.WriteLine("[App] Hotkey backend did not stop in time.");
            }

            _socketLifetime.Cancel();
            services.GetRequiredService<ILocalSocketServer>().StopAsync().GetAwaiter().GetResult();
            connector.StopAsync().GetAwaiter().GetResult();
            tray.Dispose();
        };

        if (!settings.Current.HasCompletedOnboarding)
        {
            main.Opened += OpenWizardOnFirstLaunch;
        }

        return;

        void OpenWizardOnFirstLaunch(object? sender, EventArgs e)
        {
            main.Opened -= OpenWizardOnFirstLaunch;
            (main.DataContext as MainWindowViewModel)?.OpenWizardCommand.Execute(null);
        }
    }

    private static void PruneHistory(IHistoryService history, int retentionDays)
    {
        try
        {
            history.Prune(retentionDays, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[App] Could not prune history: {ex.GetType().Name}");
        }
    }

    private async Task StartControlSocketAsync(ILocalSocketServer server, ControlCommandHandler commands)
    {
        try
        {
            await server.StartAsync(commands.HandleAsync, _socketLifetime.Token);
            Trace.WriteLine($"[App] Control socket ready at {server.Address}");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[App] Control socket did not start: {ex.Message}");
        }
    }

    private async Task StartConnectorAsync(ConnectorService connector)
    {
        try
        {
            await connector.StartAsync(_socketLifetime.Token);
            Trace.WriteLine($"[App] Browser connector ready at {connector.Address}");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[App] Browser connector did not start: {ex.Message}");
        }
    }

    private async Task StartHotkeysAsync(HotkeyService hotkeys)
    {
        try
        {
            await hotkeys.StartAsync(_socketLifetime.Token);
            Trace.WriteLine($"[App] Hotkey status {hotkeys.Status.Kind}.");
        }
        catch (OperationCanceledException)
        {
            Trace.WriteLine("[App] Hotkey start was cancelled.");
        }
    }

    private static void ShowMainWindow(MainWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        // GNOME Wayland can ignore an Activate issued in the same turn as Show.
        Dispatcher.UIThread.Post(window.Activate);
    }
}
