using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Platform.Hotkeys;
using Juvin.Platform.Mac.NativeMessaging;
using Juvin.Platform.Mac.Panel;
using Juvin.Platform.Mac.Secrets;
using Juvin.Platform.Mac.Setup;
using Juvin.Platform.Mac.Tray;
using Juvin.Platform.Setup;
using Juvin.Platform.Unix;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.Platform.Mac;

public static class PlatformServices
{
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the collection for chaining
    public static IServiceCollection AddMacPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISecretStore, KeychainSecretStore>();
        services.AddUnixLocalSocket();
        services.AddUnixConnectorSocket();
        services.AddSingleton<ITrayHostProbe, MenuBarTrayHostProbe>();
        services.AddSingleton<IPanelWindowSupport, MacPanelWindowSupport>();
        services.AddSingleton<INativeHostRegistrar>(_ => new MacNativeHostRegistrar(NativeHost.Locate));
        services.AddSingleton<IHotkeyBackend, SharpHookHotkeyBackend>();
        services.AddSingleton<ISetupTask>(sp => new AccessibilityPermissionSetupTask(
            sp.GetRequiredService<Localizer>(),
            sp.GetRequiredService<IProcessRunner>(),
            ct => sp.GetService<IHotkeyRestart>()?.RestartAsync(ct) ?? Task.CompletedTask
        ));
        return services;
    }
}
