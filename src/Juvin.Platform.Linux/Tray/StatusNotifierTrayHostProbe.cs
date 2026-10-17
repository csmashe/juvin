using System.ComponentModel;
using System.Diagnostics;
using Juvin.Core.Providers;

namespace Juvin.Platform.Linux.Tray;

/// <summary>
///     Asks the StatusNotifier watcher on the session bus whether a tray host (a KDE panel, the
///     GNOME AppIndicator extension, waybar) has registered. Avalonia's tray icon silently does
///     nothing without one. No watcher on the bus means no host; any other failure is unknown.
/// </summary>
public sealed class StatusNotifierTrayHostProbe : ITrayHostProbe
{
    private static readonly string[] s_arguments =
    [
        "call", "--session",
        "--dest", "org.kde.StatusNotifierWatcher",
        "--object-path", "/StatusNotifierWatcher",
        "--method", "org.freedesktop.DBus.Properties.Get",
        "org.kde.StatusNotifierWatcher", "IsStatusNotifierHostRegistered",
    ];

    // The bus's answers when nothing owns the watcher name, so no tray host can have registered.
    private static readonly string[] s_noWatcherErrors =
    [
        "org.freedesktop.DBus.Error.ServiceUnknown",
        "org.freedesktop.DBus.Error.NameHasNoOwner",
    ];

    private readonly IProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public StatusNotifierTrayHostProbe(IProcessRunner runner)
        : this(runner, TimeSpan.FromSeconds(2))
    {
    }

    internal StatusNotifierTrayHostProbe(IProcessRunner runner, TimeSpan timeout)
    {
        _runner = runner;
        _timeout = timeout;
    }

    public async Task<bool?> IsTrayHostAvailableAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            var result = await _runner.RunAsync("gdbus", s_arguments, null, null, timeout.Token).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                // gdbus prints "(<true>,)" when a host is registered.
                return result.StandardOutput.Contains("true", StringComparison.Ordinal);
            }

            if (s_noWatcherErrors.Any(error => result.StandardError.Contains(error, StringComparison.Ordinal)))
            {
                return false;
            }

            Trace.WriteLine($"[StatusNotifierTrayHostProbe] gdbus exited with {result.ExitCode}.");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Trace.WriteLine("[StatusNotifierTrayHostProbe] gdbus timed out.");
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Trace.WriteLine($"[StatusNotifierTrayHostProbe] Probe failed ({ex.GetType().Name}).");
            return null;
        }
    }
}
