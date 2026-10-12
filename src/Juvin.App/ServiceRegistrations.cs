using Avalonia.Threading;
using Juvin.App.Services;
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
        services.AddSingleton<Localizer>();
        AddPlatform(services);

        // Probes use this client; the rewrite panel has its own with a longer timeout.
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
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
        services.AddKeyedSingleton(RewriteHttpClientKey, (_, _) => new HttpClient { Timeout = s_rewriteTimeout });
        services.AddSingleton(sp => new RewritePanelViewModel(
            ProviderSelector.CreateDefault(
                sp.GetRequiredKeyedService<HttpClient>(RewriteHttpClientKey),
                sp.GetRequiredService<IProcessRunner>()
            ),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<Localizer>(),
            action => Dispatcher.UIThread.Post(action)
        ));
        services.AddSingleton<RewritePanelWindow>();
        services.AddSingleton(sp => new RewritePanelService(
            sp.GetRequiredService<ConnectorService>(),
            sp.GetRequiredService<RewritePanelViewModel>(),
            sp.GetRequiredService<RewritePanelWindow>(),
            action => Dispatcher.UIThread.Post(action)
        ));

        services.AddSingleton<HomeSectionViewModel>();
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
        services.AddTransient<SetupWizardViewModel>();
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
