using Juvin.Platform.Hotkeys;
using Juvin.Platform.Windows.Ipc;
using Juvin.Platform.Windows.Launcher;
using Juvin.Platform.Windows.NativeMessaging;
using Juvin.Platform.Windows.Panel;
using Juvin.Platform.Windows.Secrets;
using Juvin.Platform.Windows.Tray;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.Platform.Windows;

public static class PlatformServices
{
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the collection for chaining
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<ISingleInstanceGuard, WindowsSingleInstanceGuard>();
        services.AddSingleton<ILocalSocketServer, NamedPipeServer>();
        services.AddSingleton<ILocalSocketClient, NamedPipeClient>();
        services.AddSingleton<IConnectorSocketServer, NamedPipeConnectorServer>();
        services.AddSingleton<IConnectorSocketClient, NamedPipeConnectorClient>();
        services.AddSingleton<ITrayHostProbe, NotificationAreaTrayHostProbe>();
        services.AddSingleton<IPanelWindowSupport, WindowsPanelWindowSupport>();
        services.AddSingleton<IToolSearchPaths, WindowsToolSearchPaths>();
        services.AddSingleton<INativeHostRegistrar>(_ => new WindowsNativeHostRegistrar(NativeHost.Locate));
        // The installer carries the launcher entry.
        services.AddSingleton<ILauncherIntegration, NoLauncherIntegration>();
        services.AddSingleton<IForegroundHandoff, WindowsForegroundHandoff>();
        services.AddSingleton<ILoginItem, RunKeyLoginItem>();
        services.AddSingleton<IHotkeyBackend, SharpHookHotkeyBackend>();
        return services;
    }
}
