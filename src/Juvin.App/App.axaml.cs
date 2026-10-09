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

        // The language must be set before any view loads, because views read strings once.
        var localizer = services.GetRequiredService<Localizer>();
        localizer.SetLanguage(settings.Current.UiLanguage);
        StrExtension.Localizer = localizer;

        // The tray goes first so the General section sees whether a tray host exists.
        var tray = services.GetRequiredService<TrayIconService>();
        tray.Initialize();

        var main = services.GetRequiredService<MainWindow>();
        desktop.MainWindow = main;

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

        desktop.Exit += (_, _) =>
        {
            _exiting = true;
            _socketLifetime.Cancel();
            services.GetRequiredService<ILocalSocketServer>().StopAsync().GetAwaiter().GetResult();
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
