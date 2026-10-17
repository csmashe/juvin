using System.Diagnostics;
using Avalonia;
using Juvin.App.Services;
using Juvin.App.Services.CrashReporting;
using Juvin.Core;
using Juvin.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.App;

public static class Program
{
    /// <summary>Asks the running instance to capture, as the global hotkey does, then exits. Never starts the app.</summary>
    private const string TriggerArgument = "--trigger";

    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_crashFlushTimeout = TimeSpan.FromSeconds(2);

    private static ServiceProvider? s_services;

    public static IServiceProvider Services =>
        s_services ?? throw new InvalidOperationException("Services are built in Main.");

    [STAThread]
    public static int Main(string[] args)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());
        Trace.AutoFlush = true;
        JuvinEnvironment.EnsureDirectories();

        using var services = ServiceRegistrations.Build();
        if (args.Contains(TriggerArgument))
        {
            return TriggerRunningInstance(services.GetRequiredService<ILocalSocketClient>());
        }

        if (!services.GetRequiredService<ISingleInstanceGuard>().TryAcquire())
        {
            return args.Contains(ILoginItem.BackgroundArgument) ? 0 : ShowRunningInstance(services.GetRequiredService<ILocalSocketClient>());
        }

        s_services = services;
        var crashReporting = services.GetRequiredService<CrashReportingService>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(crashReporting, e.ExceptionObject);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // ReSharper disable once MemberCanBePrivate.Global -- the Avalonia previewer locates this by reflection and needs it public
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }

    // The process ends when this returns, so the report is flushed here rather than left in the queue.
    private static void ReportCrash(CrashReportingService crashReporting, object exceptionObject)
    {
        try
        {
            if (exceptionObject is Exception exception)
            {
                crashReporting.CaptureException(exception, CrashOperations.AppDomain, fatal: true);
            }

            crashReporting.Flush(s_crashFlushTimeout);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CrashReporting] Crash report failed: {ex.GetType().Name}");
        }
    }

    private static int TriggerRunningInstance(ILocalSocketClient client)
    {
        var reply = client
            .SendAsync(ControlCommandHandler.TriggerRequestLine, s_requestTimeout)
            .GetAwaiter()
            .GetResult();
        if (reply is null)
        {
            Trace.WriteLine("[Program] No running instance answered the trigger.");
            return 1;
        }

        Trace.WriteLine("[Program] Asked the running instance to capture.");
        return 0;
    }

    private static int ShowRunningInstance(ILocalSocketClient client)
    {
        // The instance holding the lock may not have bound its control socket yet.
        var reply = client
            .SendWhenReadyAsync(ControlCommandHandler.ShowRequestLine, s_requestTimeout)
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
