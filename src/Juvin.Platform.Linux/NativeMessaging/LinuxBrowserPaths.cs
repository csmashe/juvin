namespace Juvin.Platform.Linux.NativeMessaging;

/// <summary>The parts of the environment browser detection and registration depend on; tests point them at a temp directory.</summary>
/// <param name="Home">The user's home directory.</param>
/// <param name="ConfigHome"><c>$XDG_CONFIG_HOME</c>, or <c>~/.config</c>.</param>
/// <param name="RuntimeDirectory"><c>$XDG_RUNTIME_DIR</c>, or null when unset.</param>
/// <param name="ExecutableDirectories">Where to look for browser and <c>flatpak</c> executables.</param>
/// <param name="FlatpakAppDirectories">The user and system Flatpak installations; an app is installed when it has a directory in one of them.</param>
/// <param name="SnapDirectory">The Snap installation root; <c>bin</c> under it holds the launchers and <c>&lt;name&gt;/current</c> marks an installed snap.</param>
internal sealed record LinuxBrowserPaths(
    string Home,
    string ConfigHome,
    string? RuntimeDirectory,
    IReadOnlyList<string> ExecutableDirectories,
    IReadOnlyList<string> FlatpakAppDirectories,
    string SnapDirectory = "/snap"
)
{
    public static LinuxBrowserPaths FromEnvironment()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        return new LinuxBrowserPaths(
            home,
            string.IsNullOrEmpty(configHome) ? Path.Join(home, ".config") : configHome,
            string.IsNullOrEmpty(runtimeDirectory) ? null : runtimeDirectory,
            // A desktop session started without the snap profile script still has Snap Firefox.
            [.. path.Split(':', StringSplitOptions.RemoveEmptyEntries), "/snap/bin"],
            [
                Path.Join(string.IsNullOrEmpty(dataHome) ? Path.Join(home, ".local", "share") : dataHome, "flatpak", "app"),
                "/var/lib/flatpak/app",
            ]
        );
    }

    /// <summary>Ubuntu's deb packages are wrappers that launch the Snap, so an installed snap counts even when the launcher is elsewhere.</summary>
    public bool IsSnap(string executable, string snapName) =>
        executable.StartsWith(Path.Join(SnapDirectory, "bin") + "/", StringComparison.Ordinal)
        || Directory.Exists(Path.Join(SnapDirectory, snapName, "current"));

    public bool IsFlatpakInstalled(string flatpakId) =>
        FlatpakAppDirectories.Any(directory => Directory.Exists(Path.Join(directory, flatpakId)));

    public string? FindExecutable(string name)
    {
        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        foreach (var directory in ExecutableDirectories)
        {
            var candidate = Path.Join(directory, name);
            try
            {
                if (File.Exists(candidate) && (File.GetUnixFileMode(candidate) & anyExecute) != 0)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable PATH entry is the same as a missing one.
            }
        }

        return null;
    }
}
