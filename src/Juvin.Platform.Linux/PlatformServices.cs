using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Platform.Hotkeys;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;
using Juvin.Platform.Linux.Hotkeys.Evdev;
using Juvin.Platform.Linux.Launcher;
using Juvin.Platform.Linux.NativeMessaging;
using Juvin.Platform.Linux.Panel;
using Juvin.Platform.Linux.Secrets;
using Juvin.Platform.Linux.Setup;
using Juvin.Platform.Linux.Tray;
using Juvin.Platform.Setup;
using Juvin.Platform.Unix;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.Platform.Linux;

public static class PlatformServices
{
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the collection for chaining
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISecretStore, LinuxSecretStore>();
        services.AddUnixLocalSocket();
        services.AddUnixConnectorSocket();
        services.AddSingleton<ITrayHostProbe, StatusNotifierTrayHostProbe>();
        services.AddSingleton<IPanelWindowSupport>(sp =>
        {
            var runner = sp.GetRequiredService<IProcessRunner>();
            return new SwayPanelWindowSupport(new HyprlandPanelWindowSupport(new X11PanelWindowSupport(), runner), runner);
        });
        services.AddSingleton<INativeHostRegistrar>(_ => new LinuxNativeHostRegistrar(NativeHost.Locate));
        services.AddSingleton<ILauncherIntegration>(_ => new AppImageLauncherIntegration(
            Environment.GetEnvironmentVariable,
            Environment.ProcessPath,
            DesktopSession.FromEnvironment().DataHome
        ));
        services.AddSingleton<ILoginItem>(_ => new XdgAutostartLoginItem(
            Environment.GetEnvironmentVariable,
            Environment.ProcessPath,
            DesktopSession.FromEnvironment().ConfigHome
        ));

        // Preference order: evdev on any session with keyboard access, the hook on X11, then the desktop's own shortcut.
        services.AddSingleton<IHotkeyBackend, EvdevHotkeyBackend>();
        services.AddSingleton<IHotkeyBackend, SharpHookHotkeyBackend>();
        services.AddSingleton<IDesktopShortcutWriter, GnomeShortcutWriter>();
        services.AddSingleton<IDesktopShortcutWriter, KdeShortcutWriter>();
        services.AddSingleton<IDesktopShortcutWriter, HyprlandShortcutWriter>();
        services.AddSingleton<IDesktopShortcutWriter, SwayShortcutWriter>();
        services.AddSingleton<IHotkeyBackend, DesktopShortcutHotkeyBackend>();

        // The app registers an IHotkeyRestart; it is looked up when access is granted so the task can be built first.
        services.AddSingleton<ISetupTask>(sp => new KeyboardAccessSetupTask(
            sp.GetRequiredService<Localizer>(),
            sp.GetRequiredService<IProcessRunner>(),
            ct => sp.GetService<IHotkeyRestart>()?.RestartAsync(ct) ?? Task.CompletedTask
        ));
        return services;
    }
}
