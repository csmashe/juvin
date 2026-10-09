using Juvin.Platform.Unix.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.Platform.Unix;

public static class UnixPlatformServices
{
    /// <summary>Registers the control socket server, client, and single-instance guard shared by Linux and macOS.</summary>
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the collection so registrations can chain
    // ReSharper disable once ConvertToExtensionBlock -- the codebase uses classic extension methods
    public static IServiceCollection AddUnixLocalSocket(this IServiceCollection services)
    {
        services.AddSingleton<UnixSingleInstanceGuard>();
        services.AddSingleton<ISingleInstanceGuard>(sp => sp.GetRequiredService<UnixSingleInstanceGuard>());
        services.AddSingleton<ILocalSocketServer>(sp =>
            new UnixSocketServer(sp.GetRequiredService<UnixSingleInstanceGuard>())
        );
        services.AddSingleton<ILocalSocketClient, UnixSocketClient>();
        return services;
    }

    /// <summary>Registers the browser connector socket server and client shared by Linux and macOS.</summary>
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the collection so registrations can chain
    public static IServiceCollection AddUnixConnectorSocket(this IServiceCollection services)
    {
        services.AddSingleton<IConnectorSocketServer, UnixConnectorSocketServer>();
        services.AddSingleton<IConnectorSocketClient, UnixConnectorSocketClient>();
        return services;
    }
}
