using System.ComponentModel;
using System.Diagnostics;

namespace Juvin.Platform.Linux.Tray;

/// <summary>
///     Asks the StatusNotifier watcher on the session bus whether a tray host (a KDE panel, the
///     GNOME AppIndicator extension, waybar) has registered. Avalonia's tray icon silently does
///     nothing without one. Any failure counts as no host.
/// </summary>
public sealed class StatusNotifierTrayHostProbe : ITrayHostProbe
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    public bool IsTrayHostAvailable()
    {
        var startInfo = new ProcessStartInfo("gdbus")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            var argument in new[]
            {
                "call", "--session",
                "--dest", "org.kde.StatusNotifierWatcher",
                "--object-path", "/StatusNotifierWatcher",
                "--method", "org.freedesktop.DBus.Properties.Get",
                "org.kde.StatusNotifierWatcher", "IsStatusNotifierHostRegistered",
            }
        )
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            // ReSharper disable once InvertIf -- the timeout branch reads as the exceptional path
            if (!process.WaitForExit(s_timeout))
            {
                process.Kill(entireProcessTree: true);
                Trace.WriteLine("[StatusNotifierTrayHostProbe] gdbus timed out.");
                return false;
            }

            // gdbus prints "(<true>,)" when a host is registered.
            return process.ExitCode == 0 && output.Result.Contains("true", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Trace.WriteLine($"[StatusNotifierTrayHostProbe] Probe failed: {ex.Message}");
            return false;
        }
    }
}
