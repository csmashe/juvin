namespace Juvin.Platform.Linux.NativeMessaging;

/// <summary>The parts of the environment browser detection and registration depend on; tests point them at a temp directory.</summary>
/// <param name="Home">The user's home directory.</param>
/// <param name="ConfigHome"><c>$XDG_CONFIG_HOME</c>, or <c>~/.config</c>.</param>
/// <param name="RuntimeDirectory"><c>$XDG_RUNTIME_DIR</c>, or null when unset.</param>
/// <param name="ExecutableDirectories">Where to look for browser and <c>flatpak</c> executables.</param>
/// <param name="FlatpakAppDirectories">The user and system Flatpak installations; an app is installed when it has a directory in one of them.</param>
/// <param name="SnapDirectory">The Snap installation root; <c>bin</c> under it holds the launchers.</param>
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
        // The XDG spec says a relative path is invalid and must be ignored, as .NET's special folders do.
        var configHome = AbsoluteOrNull(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        var dataHome = AbsoluteOrNull(Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        return new LinuxBrowserPaths(
            home,
            configHome ?? Path.Join(home, ".config"),
            string.IsNullOrEmpty(runtimeDirectory) ? null : runtimeDirectory,
            // A desktop session started without the snap profile script still has Snap Firefox.
            [.. path.Split(':', StringSplitOptions.RemoveEmptyEntries), "/snap/bin"],
            [
                Path.Join(dataHome ?? Path.Join(home, ".local", "share"), "flatpak", "app"),
                "/var/lib/flatpak/app",
            ]
        );
    }

    /// <summary>
    ///     Whether <paramref name="executable" /> starts a Snap: a launcher in the Snap <c>bin</c> directory,
    ///     a symlink into the Snap root, or one of the small scripts Ubuntu's transitional debs install
    ///     in <c>/usr/bin</c> to run the Snap. An installed Snap of the same browser alone does not count,
    ///     since a vendor package can sit beside it.
    /// </summary>
    public bool IsSnap(string executable)
    {
        const int maxLinks = 8;
        try
        {
            // Fedora mounts Snaps at /var/lib/snapd/snap and makes /snap a symlink to it, so both roots count.
            var root = Path.TrimEndingDirectorySeparator(SnapDirectory);
            var snapRoots = new List<string> { root + "/" };
            if (Directory.Exists(root) && Directory.ResolveLinkTarget(root, returnFinalTarget: true) is { } mount)
            {
                snapRoots.Add(Path.TrimEndingDirectorySeparator(mount.FullName) + "/");
            }

            // Each hop counts: Snap launchers are themselves symlinks to /usr/bin/snap.
            var current = executable;
            for (var hop = 0; hop <= maxLinks; hop++)
            {
                if (snapRoots.Any(snapRoot => current.StartsWith(snapRoot, StringComparison.Ordinal)))
                {
                    return true;
                }

                if (File.ResolveLinkTarget(current, returnFinalTarget: false) is not { } target)
                {
                    break;
                }

                current = target.FullName;
            }

            return Path.GetFileName(current) == "snap" || IsSnapWrapperScript(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool IsFlatpakInstalled(string flatpakId) =>
        FlatpakAppDirectories.Any(directory => Directory.Exists(Path.Join(directory, flatpakId)));

    public string? FindExecutable(string name)
    {
        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        foreach (var candidate in ExecutableDirectories.Select(directory => Path.Join(directory, name)))
        {
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

    private static string? AbsoluteOrNull(string? path) => Path.IsPathRooted(path) ? path : null;

    private static bool IsSnapWrapperScript(string path)
    {
        const int maxScriptBytes = 4096;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > maxScriptBytes)
        {
            return false;
        }

        var text = File.ReadAllText(path);
        return text.StartsWith("#!", StringComparison.Ordinal)
            && (text.Contains("/snap/", StringComparison.Ordinal) || text.Contains("snap run", StringComparison.Ordinal));
    }
}
