using Juvin.App.Services;
using Juvin.App.ViewModels;
using Juvin.App.ViewModels.Sections;
using Juvin.App.Views;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Core.Services;
using Juvin.Platform.Linux;
using Juvin.Platform.Mac;
using Juvin.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.App;

public static class ServiceRegistrations
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<Localizer>();
        AddPlatform(services);

        // Probes use this client; the rewrite path sets its own timeout when it lands.
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(sp =>
            ProviderSelector.CreateDefault(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IProcessRunner>())
        );
        services.AddSingleton<ModelProbeService>();

        services.AddSingleton<TrayIconService>();
        services.AddSingleton<ControlCommandHandler>();

        services.AddSingleton<HomeSectionViewModel>();
        services.AddSingleton<ShortcutsSectionViewModel>();
        services.AddSingleton<RewriteActionsSectionViewModel>();
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
