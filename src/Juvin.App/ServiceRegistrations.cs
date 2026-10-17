using Avalonia.Threading;
using Juvin.App.Services;
using Juvin.App.Services.CrashReporting;
using Juvin.App.ViewModels;
using Juvin.App.ViewModels.Sections;
using Juvin.App.Views;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Core.Services;
using Juvin.Platform.Hotkeys;
using Juvin.Platform.Linux;
using Juvin.Platform.Mac;
using Juvin.Platform.Setup;
using Juvin.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.App;

public static class ServiceRegistrations
{
    private const string RewriteHttpClientKey = "rewrite";
    private static readonly TimeSpan s_rewriteTimeout = TimeSpan.FromMinutes(5);

    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<Localizer>();
        services.AddSingleton<CrashReportingService>();
        services.AddSingleton<ICrashReporter>(sp => sp.GetRequiredService<CrashReportingService>());
        AddPlatform(services);

        // Probes use this client and selector; rewrites have their own with a longer timeout.
        services.AddSingleton(_ => ModelHttpClient.Create(TimeSpan.FromSeconds(30)));
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(sp =>
            ProviderSelector.CreateDefault(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IProcessRunner>())
        );
        services.AddSingleton<ModelProbeService>();

        services.AddSingleton<TrayIconService>();
        services.AddSingleton<ControlCommandHandler>();
        services.AddSingleton<ConnectorService>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<IHotkeyRestart>(sp => sp.GetRequiredService<HotkeyService>());

        // Rewrites get a longer idle limit than probes: a local model can take minutes to load.
        services.AddKeyedSingleton(RewriteHttpClientKey, (_, _) => ModelHttpClient.Create(s_rewriteTimeout));
        services.AddKeyedSingleton(
            RewriteHttpClientKey,
            (sp, _) =>
                ProviderSelector.CreateDefault(
                    sp.GetRequiredKeyedService<HttpClient>(RewriteHttpClientKey),
                    sp.GetRequiredService<IProcessRunner>()
                )
        );
        services.AddSingleton(sp => new RewritePanelViewModel(
            sp.GetRequiredKeyedService<ProviderSelector>(RewriteHttpClientKey),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IHistoryService>(),
            sp.GetRequiredService<Localizer>(),
            action => Dispatcher.UIThread.Post(action)
        ));
        services.AddSingleton<RewritePanelWindow>();
        services.AddSingleton(sp => new RewritePanelService(
            sp.GetRequiredService<RewritePanelViewModel>(),
            sp.GetRequiredService<RewritePanelWindow>(),
            action => Dispatcher.UIThread.Post(action)
        ));
        services.AddSingleton(sp => new ActionMenuService(
            sp.GetRequiredService<ConnectorService>(),
            sp.GetRequiredService<RewritePanelService>(),
            sp.GetRequiredKeyedService<ProviderSelector>(RewriteHttpClientKey),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IHistoryService>(),
            sp.GetRequiredService<Localizer>(),
            action => Dispatcher.UIThread.Post(action)
        ));

        services.AddSingleton(sp => new HomeSectionViewModel(
            sp.GetRequiredService<IHistoryService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ConnectorService>(),
            sp.GetRequiredService<HotkeyService>(),
            sp.GetRequiredService<ModelProbeService>(),
            sp.GetRequiredService<Localizer>(),
            action => Dispatcher.UIThread.Post(action)
        ));
        services.AddTransient(sp => new ShortcutPanelViewModel(
            sp.GetRequiredService<HotkeyService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<Localizer>(),
            action => Dispatcher.UIThread.Post(action)
        ));
        services.AddSingleton<ShortcutsSectionViewModel>();
        services.AddSingleton<RewriteActionsSectionViewModel>();
        services.AddTransient<BrowserConnectorPanelViewModel>();
        services.AddSingleton<BrowserConnectorSectionViewModel>();
        services.AddSingleton<ModelsSectionViewModel>();
        services.AddSingleton<PrivacyAndDataSectionViewModel>();
        services.AddSingleton<GeneralSectionViewModel>();
        services.AddSingleton<AboutSectionViewModel>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient(sp => new SetupWizardViewModel(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<Localizer>(),
            sp.GetRequiredService<ModelProbeService>(),
            sp.GetRequiredService<BrowserConnectorPanelViewModel>(),
            sp.GetRequiredService<ShortcutPanelViewModel>(),
            sp.GetRequiredService<ConnectorService>(),
            sp.GetRequiredService<RewritePanelViewModel>(),
            sp.GetRequiredService<ActionMenuService>(),
            sp.GetRequiredService<ICrashReporter>(),
            sp.GetServices<ISetupTask>(),
            action => Dispatcher.UIThread.Post(action)
        ));
        services.AddTransient<SetupWizard>();

        return services.BuildServiceProvider();
    }

    private static void AddPlatform(IServiceCollection services)
    {
        if (OperatingSystem.IsLinux())
        {
            services.AddLinuxPlatform();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddMacPlatform();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddWindowsPlatform();
        }
        else
        {
            throw new PlatformNotSupportedException("Juvin runs on Linux, macOS, and Windows.");
        }
    }
}
