using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Juvin.Platform.Mac.Launcher;

/// <summary>Starts the app again in a new process, behind a seam so the setup task is testable off macOS.</summary>
internal interface IAppRelauncher
{
    /// <summary>False when the app does not run from a bundle, as under <c>dotnet run</c>.</summary>
    bool CanRelaunch { get; }

    /// <summary>Opens the bundle again once this process has ended, then asks the app to exit. False when the launch could not be set up.</summary>
    bool Relaunch();
}

/// <summary>Reopens <c>Juvin.app</c> through LaunchServices after this process exits.</summary>
internal sealed class MacAppRelauncher : IAppRelauncher
{
    // A copy opened while this one still holds the single-instance lock would only hand over to it, so
    // the shell waits up to a minute for this process to end and opens nothing if it is still running.
    // The wait runs as a job in its own process group, because launchd ends the group of a Juvin it
    // started at login when that Juvin exits.
    internal const string Script =
        "set -m; ( i=0; while kill -0 \"$1\" 2>/dev/null && [ \"$i\" -lt 600 ]; do sleep 0.1; i=$((i+1)); done; kill -0 \"$1\" 2>/dev/null || exec /usr/bin/open -n \"$2\" ) </dev/null >/dev/null 2>&1 &";

    private readonly string? _bundlePath;
    private readonly int _processId;
    private readonly Func<ProcessStartInfo, bool> _start;
    private readonly Action _requestExit;

    public MacAppRelauncher(Action requestExit)
        : this(Environment.ProcessPath, Environment.ProcessId, StartDetached, requestExit)
    {
    }

    internal MacAppRelauncher(string? processPath, int processId, Func<ProcessStartInfo, bool> start, Action requestExit)
    {
        _bundlePath = BundlePath(processPath);
        _processId = processId;
        _start = start;
        _requestExit = requestExit;
    }

    public bool CanRelaunch => _bundlePath is not null;

    public bool Relaunch()
    {
        if (_bundlePath is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            ArgumentList = { "-c", Script, "sh", _processId.ToString(CultureInfo.InvariantCulture), _bundlePath },
        };
        if (!_start(startInfo))
        {
            Trace.WriteLine("[MacAppRelauncher] The relaunch could not be started.");
            return false;
        }

        _requestExit();
        return true;
    }

    /// <summary>The <c>.app</c> folder around <c>Contents/MacOS/&lt;executable&gt;</c>, or null outside a bundle.</summary>
    internal static string? BundlePath(string? processPath)
    {
        // Split on '/' rather than through Path, which uses the host OS's separators and would misread the path in tests elsewhere.
        var parts = processPath?.Split('/') ?? [];
        var n = parts.Length;
        return n >= 4 && parts[n - 2] == "MacOS" && parts[n - 3] == "Contents" && parts[n - 4].Length > ".app".Length && parts[n - 4].EndsWith(".app", StringComparison.Ordinal)
            ? string.Join('/', parts[..(n - 3)])
            : null;
    }

    private static bool StartDetached(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
