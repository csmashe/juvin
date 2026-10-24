using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Juvin.App.Localization;
using Juvin.App.Services;
using Juvin.App.Services.CrashReporting;
using Juvin.App.ViewModels;
using Juvin.App.ViewModels.Sections;
using Juvin.App.Views;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.App;

public class App : Application
{
    private static readonly TimeSpan s_hotkeyStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_menuCloseTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_launcherInstallTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_crashFlushTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_trayRecheckDelay = TimeSpan.FromSeconds(5);
    private const int TrayRecheckAttempts = 12;
    private readonly CancellationTokenSource _socketLifetime = new();
    private bool _exiting;
    private bool _hidingToTray;

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
        var crashReporting = services.GetRequiredService<CrashReportingService>();
        StartCrashReporting(crashReporting);
        PruneHistory(services.GetRequiredService<IHistoryService>(), settings.Current.HistoryRetentionDays);

        // The language must be set before any view loads, because views read strings once.
        var localizer = services.GetRequiredService<Localizer>();
        localizer.SetLanguage(settings.Current.UiLanguage);
        StrExtension.Localizer = localizer;

        // The tray goes first so the General section sees whether a tray host exists.
        var tray = services.GetRequiredService<TrayIconService>();
        tray.Initialize();

        // GNOME matches a window to its desktop entry when the window first maps, so the entry must exist by then.
        InstallLauncher(services.GetRequiredService<ILauncherIntegration>());
        var main = services.GetRequiredService<MainWindow>();
        // A login start stays in the tray. The lifetime shows MainWindow before its loop runs, so it is assigned once the loop starts.
        var backgroundStart = desktop.Args?.Contains(ILoginItem.BackgroundArgument) == true;
        var startHidden = backgroundStart && tray.IsTrayAvailable && settings.Current.HasCompletedOnboarding;
        if (startHidden)
        {
            Dispatcher.UIThread.Post(() => desktop.MainWindow = main);
        }
        else
        {
            desktop.MainWindow = main;
        }

        // At login the tray host may start after the app, sometimes well after.
        if (backgroundStart && !tray.IsTrayAvailable)
        {
            _ = RefreshTrayLaterAsync(tray);
        }

        // The rewrite panel stays open while hidden, so it must not keep the app alive.
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        main.Closing += (sender, e) =>
        {
            if (_exiting || e.CloseReason != WindowCloseReason.WindowClosing || !settings.Current.CloseToTray)
            {
                return;
            }

            // The tray host may have started or stopped since the last probe, which answers asynchronously.
            e.Cancel = true;
            if (!_hidingToTray)
            {
                _ = HideToTrayOrCloseAsync((MainWindow)sender!, tray);
            }
        };

        tray.ShowSettingsRequested += (_, _) => ShowMainWindow(main);
        if (NativeMenu.GetMenu(this)?.Items[0] is NativeMenuItem about)
        {
            about.Header = localizer["App.AboutMenu"];
            about.Click += (_, _) =>
            {
                ShowMainWindow(main);
                (main.DataContext as MainWindowViewModel)?.Navigate<AboutSectionViewModel>();
            };
        }
        // A Dock icon click on macOS, sent to this process rather than to a second launch.
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    Dispatcher.UIThread.Post(() => ShowMainWindow(main));
                }
            };
        }

        tray.ExitRequested += (_, _) =>
        {
            _exiting = true;
            desktop.Shutdown();
        };
        services.GetRequiredService<AppExitService>().ExitRequested += () =>
            Dispatcher.UIThread.Post(() =>
            {
                _exiting = true;
                desktop.Shutdown();
            });

        var loginItem = services.GetRequiredService<ILoginItem>();
        if (settings.Current.LaunchAtLogin && loginItem.IsSupported)
        {
            RefreshLoginItem(loginItem);
        }

        // The panel and menu subscribe before either socket starts so no early capture or trigger goes unanswered.
        var panel = services.GetRequiredService<RewritePanelService>();
        panel.Start();
        var menu = services.GetRequiredService<ActionMenuService>();
        menu.Start();

        // A desktop environment's own shortcut runs `juvin --trigger`, which arrives as a press here.
        var commands = services.GetRequiredService<ControlCommandHandler>();
        var hotkeys = services.GetRequiredService<HotkeyService>();
        commands.ShowRequested += () => Dispatcher.UIThread.Post(() => ShowMainWindow(main));
        commands.TriggerRequested += hotkeys.HandleTrigger;
        _ = StartControlSocketAsync(services.GetRequiredService<ILocalSocketServer>(), commands);

        var connector = services.GetRequiredService<ConnectorService>();
        _ = StartConnectorAsync(connector);

        _ = StartHotkeysAsync(hotkeys);

        desktop.Exit += (_, _) =>
        {
            _exiting = true;
            // Bounded: the menu's close frame must go out before the socket closes, but a stalled browser must not hold exit.
            var menuClosed = menu.Stop();
            panel.Stop();
            // Bounded: a backend may be mid-way through a desktop command, and shutdown must not hang on it.
            if (!hotkeys.StopAsync().Wait(s_hotkeyStopTimeout))
            {
                Trace.WriteLine("[App] Hotkey backend did not stop in time.");
            }

            if (!menuClosed.Wait(s_menuCloseTimeout))
            {
                Trace.WriteLine("[App] The action menu close was not sent in time.");
            }

            _socketLifetime.Cancel();
            services.GetRequiredService<ILocalSocketServer>().StopAsync().GetAwaiter().GetResult();
            connector.StopAsync().GetAwaiter().GetResult();
            tray.Dispose();
            ShutDownCrashReporting(crashReporting);
        };

        if (!settings.Current.HasCompletedOnboarding)
        {
            main.Opened += OpenWizardOnFirstLaunch;
        }

        return;

        void OpenWizardOnFirstLaunch(object? sender, EventArgs e)
        {
            main.Opened -= OpenWizardOnFirstLaunch;
            // After the main window's own show and activation, so the wizard ends up in front of it.
            Dispatcher.UIThread.Post(() => (main.DataContext as MainWindowViewModel)?.OpenWizardCommand.Execute(null));
        }
    }

    // Left unhandled so the crash still ends the process; flushed here because the AppDomain hook may not run for a dispatcher exception.
    private static void StartCrashReporting(CrashReportingService crashReporting)
    {
        try
        {
            crashReporting.Start();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CrashReporting] Start failed: {ex.GetType().Name}");
        }

        Dispatcher.UIThread.UnhandledException += (_, e) => Capture(e.Exception, CrashOperations.UiDispatcher, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) => Capture(e.Exception, CrashOperations.UnobservedTask, fatal: false);
        return;

        void Capture(Exception exception, string operation, bool fatal)
        {
            try
            {
                crashReporting.CaptureException(exception, operation, fatal);
                if (fatal)
                {
                    crashReporting.Flush(s_crashFlushTimeout);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CrashReporting] Capture from {operation} failed: {ex.GetType().Name}");
            }
        }
    }

    private static void ShutDownCrashReporting(CrashReportingService crashReporting)
    {
        try
        {
            crashReporting.Shutdown();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CrashReporting] Shutdown failed: {ex.GetType().Name}");
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
        catch (ArgumentException ex)
        {
            Trace.WriteLine($"[App] Control socket did not start: {ex.GetType().Name}");
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
        catch (ArgumentException ex)
        {
            Trace.WriteLine($"[App] Browser connector did not start: {ex.GetType().Name}");
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

    // Rewritten on every start so a moved AppImage or a reinstall keeps the entry current, and before the window exists so it cannot overlap a change from General.
    private static void RefreshLoginItem(ILoginItem loginItem)
    {
        try
        {
            if (!loginItem.EnableAsync(CancellationToken.None).Wait(s_launcherInstallTimeout))
            {
                Trace.WriteLine("[App] Login item refresh did not finish in time.");
            }
        }
        catch (AggregateException ex) when (ex.InnerException is LoginItemException failure)
        {
            Trace.WriteLine($"[App] Could not refresh the login item: {failure.InnerException?.GetType().Name ?? failure.GetType().Name}");
        }
    }

    // Bounded: the entry is cosmetic and must not hold up startup on a slow disk.
    private static void InstallLauncher(ILauncherIntegration launcher)
    {
        if (!launcher.EnsureInstalledAsync(CancellationToken.None).Wait(s_launcherInstallTimeout))
        {
            Trace.WriteLine("[App] Launcher entry install did not finish in time.");
        }
    }

    private async Task HideToTrayOrCloseAsync(MainWindow main, TrayIconService tray)
    {
        _hidingToTray = true;
        try
        {
            // A probe that cannot tell keeps the last answer, so a busy session bus does not turn this into a quit.
            if (await tray.RefreshAsync())
            {
                main.Hide();
                return;
            }
        }
        finally
        {
            _hidingToTray = false;
        }

        _exiting = true;
        main.Close();
    }

    /// <summary>A tray host that starts after a login start is picked up within the first minute.</summary>
    private static async Task RefreshTrayLaterAsync(TrayIconService tray)
    {
        for (var attempt = 0; attempt < TrayRecheckAttempts; attempt++)
        {
            await Task.Delay(s_trayRecheckDelay);
            if (await tray.RefreshAsync())
            {
                return;
            }
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

        Activate(window);
        // GNOME Wayland can ignore an Activate issued in the same turn as Show.
        Dispatcher.UIThread.Post(() => Activate(window));
    }

    // Activating the main window on macOS orders it above its modal setup wizard, so the wizard goes in front again.
    private static void Activate(MainWindow window)
    {
        window.Activate();
        (window.DataContext as MainWindowViewModel)?.ActivateOpenWizard();
    }
}
