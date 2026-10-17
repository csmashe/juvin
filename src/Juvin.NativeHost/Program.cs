using System.Diagnostics;
using Juvin.Core.Connector;
using Juvin.Platform;
using Juvin.Platform.Unix.Ipc;
using Juvin.Platform.Windows.Ipc;

namespace Juvin.NativeHost;

/// <summary>
///     The native messaging host the browser launches. It relays bytes between the browser and the
///     running app's connector endpoint and nothing else; it never parses or logs a message.
/// </summary>
public static class Program
{
    private static readonly TimeSpan s_connectWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_attemptTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(100);

    // ReSharper disable once UnusedParameter.Global -- browsers pass the manifest path or extension origin, which the host does not need
    public static async Task<int> Main(string[] args)
    {
        Stream? app;
        try
        {
            app = await ConnectAsync(CreateClient()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Resolving the socket path can fail when the runtime directory is not private.
            app = null;
        }

        if (app is null)
        {
            await Console.Error.WriteLineAsync("juvin-host: could not connect to Juvin. Is the app running?").ConfigureAwait(false);
            return 1;
        }

        await using (app.ConfigureAwait(false))
        {
            var browserInput = Console.OpenStandardInput();
            var browserOutput = Console.OpenStandardOutput();
            await ConnectorRelay.RunAsync(browserInput, browserOutput, app, CancellationToken.None).ConfigureAwait(false);
        }

        return 0;
    }

    private static IConnectorSocketClient CreateClient()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return new UnixConnectorSocketClient();
        }

        // ReSharper disable once ConvertIfStatementToReturnStatement -- a guard clause reads better than a throw inside a conditional
        if (OperatingSystem.IsWindows())
        {
            return new NamedPipeConnectorClient();
        }

        throw new PlatformNotSupportedException("Juvin runs on Linux, macOS, and Windows.");
    }

    /// <summary>Retries for a short window so a browser started together with the app still connects.</summary>
    private static async Task<Stream?> ConnectAsync(IConnectorSocketClient client)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var stream = await client.ConnectAsync(s_attemptTimeout).ConfigureAwait(false);
            if (stream is not null || Stopwatch.GetElapsedTime(started) >= s_connectWindow)
            {
                return stream;
            }

            await Task.Delay(s_retryDelay).ConfigureAwait(false);
        }
    }
}
