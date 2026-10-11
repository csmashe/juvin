using Juvin.Platform.Linux.NativeMessaging;
using Juvin.Platform.Linux.Panel;
using Juvin.Platform.Linux.Secrets;
using Juvin.Platform.Linux.Tray;
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
        services.AddSingleton<IPanelWindowSupport, X11PanelWindowSupport>();
        services.AddSingleton<INativeHostRegistrar>(_ => new LinuxNativeHostRegistrar(NativeHost.Locate));
        return services;
    }
}
