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
        services.AddSingleton<ITrayHostProbe, MenuBarTrayHostProbe>();
        return services;
    }
}
