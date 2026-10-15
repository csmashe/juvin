using System.Diagnostics;
using System.Globalization;
using Juvin.Core.Services;

namespace Juvin.Platform.Linux.Launcher;

/// <summary>
///     An XDG autostart entry in <c>$XDG_CONFIG_HOME/autostart</c>, which GNOME, KDE, and most other
///     desktops start at login. A file there without the marker line is someone else's and is never removed.
/// </summary>
public sealed class XdgAutostartLoginItem : ILoginItem
{
    private const string ManagedMarker = "X-Juvin-Managed=true";

    private const string EntryTemplate = """
        [Desktop Entry]
        Type=Application
        Name=Juvin
        Comment=Works beside you
        Exec={0} {1}
        Icon=juvin
        Terminal=false
        StartupNotify=false
        StartupWMClass=juvin
        Hidden=false
        X-GNOME-Autostart-enabled=true
        {2}

        """;

    private readonly string? _executable;
    private readonly string _entryPath;

    /// <param name="getEnvironment">Reads <c>APPDIR</c> and <c>APPIMAGE</c>, so an AppImage entry names the <c>.AppImage</c> file.</param>
    /// <param name="processPath">The running executable.</param>
    /// <param name="configHome"><c>$XDG_CONFIG_HOME</c>, or <c>~/.config</c>.</param>
    public XdgAutostartLoginItem(Func<string, string?> getEnvironment, string? processPath, string configHome)
    {
        _executable = LinuxProcessPath.Resolve(getEnvironment, processPath);
        _entryPath = Path.Join(configHome, "autostart", "juvin.desktop");
    }

    public bool IsSupported => _executable is not null;

    public Task EnableAsync(CancellationToken ct) => Task.Run(Enable, ct);

    public Task DisableAsync(CancellationToken ct) => Task.Run(Disable, ct);

    private bool IsOurs() => File.Exists(_entryPath) && File.ReadLines(_entryPath).Any(line => line.Trim() == ManagedMarker);

    private void Enable()
    {
        var executable = _executable ?? throw new LoginItemException("The app's executable path is unknown.");
        if (executable.Contains('\n') || executable.Contains('\r'))
        {
            throw new LoginItemException("The app's path contains a line break.");
        }

        var entry = string.Format(CultureInfo.InvariantCulture, EntryTemplate, DesktopEntryExec.Quote(executable), ILoginItem.BackgroundArgument, ManagedMarker);
        try
        {
            if (File.Exists(_entryPath) && File.ReadAllText(_entryPath) == entry)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_entryPath)!);
            AtomicFile.WriteAllText(_entryPath, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LoginItemException($"Could not write the autostart entry: {ex.Message}", ex);
        }

        Trace.WriteLine("[Autostart] Wrote the autostart entry.");
    }

    private void Disable()
    {
        try
        {
            if (!IsOurs())
            {
                return;
            }

            File.Delete(_entryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LoginItemException($"Could not remove the autostart entry: {ex.Message}", ex);
        }

        Trace.WriteLine("[Autostart] Removed the autostart entry.");
    }
}
