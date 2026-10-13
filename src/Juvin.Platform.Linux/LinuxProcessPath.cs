namespace Juvin.Platform.Linux;

/// <summary>
///     Where the app runs from, for files that outlive the process. An AppImage runs from a mount
///     that moves on every launch, so the path written out is the <c>.AppImage</c> file itself.
/// </summary>
internal static class LinuxProcessPath
{
    private const string AppImageVariable = "APPIMAGE";
    private const string AppDirVariable = "APPDIR";

    /// <summary>
    ///     True when this process runs from an AppImage mount. Both variables are inherited by every
    ///     process an AppImage starts, a terminal's shell included, so the executable must also live
    ///     under <c>APPDIR</c>.
    /// </summary>
    public static bool RunsFromAppImage(Func<string, string?> getEnvironment, string? processPath)
    {
        var appDir = getEnvironment(AppDirVariable);
        return !string.IsNullOrEmpty(appDir)
            && !string.IsNullOrEmpty(getEnvironment(AppImageVariable))
            && processPath is not null
            && processPath.StartsWith(Path.TrimEndingDirectorySeparator(appDir) + "/", StringComparison.Ordinal);
    }

    public static string? Resolve(Func<string, string?> getEnvironment, string? processPath) =>
        RunsFromAppImage(getEnvironment, processPath) ? getEnvironment(AppImageVariable) : processPath;
}
