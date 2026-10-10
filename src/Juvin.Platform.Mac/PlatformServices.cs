using Juvin.Platform.Mac.NativeMessaging;
using Juvin.Platform.Mac.Secrets;
using Juvin.Platform.Mac.Tray;
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
        services.AddSingleton<INativeHostRegistrar>(_ => new MacNativeHostRegistrar(NativeHost.Locate));
        return services;
    }
}
