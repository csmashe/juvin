using System.Diagnostics;
using Avalonia;
using Juvin.App.Services;
using Juvin.Core;
using Juvin.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.App;

public static class Program
{
    private static readonly TimeSpan s_showRequestTimeout = TimeSpan.FromSeconds(3);

    private static ServiceProvider? s_services;

    public static IServiceProvider Services =>
        s_services ?? throw new InvalidOperationException("Services are built in Main.");

    [STAThread]
    public static int Main(string[] args)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());
        JuvinEnvironment.EnsureDirectories();

        using var services = ServiceRegistrations.Build();
        if (!services.GetRequiredService<ISingleInstanceGuard>().TryAcquire())
        {
            return ShowRunningInstance(services.GetRequiredService<ILocalSocketClient>());
        }

        s_services = services;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // ReSharper disable once MemberCanBePrivate.Global -- the Avalonia previewer locates this by reflection and needs it public
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }

    private static int ShowRunningInstance(ILocalSocketClient client)
    {
        var reply = client
            .SendAsync(ControlCommandHandler.ShowRequestLine, s_showRequestTimeout)
            .GetAwaiter()
            .GetResult();
        if (reply is null)
        {
            Trace.WriteLine("[Program] Another instance holds the lock but did not answer.");
            return 1;
        }

        Trace.WriteLine("[Program] Asked the running instance to show its window.");
        return 0;
    }
}
