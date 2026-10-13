using System.Diagnostics;
using System.Globalization;
using System.Text;
using Juvin.Core.Services;

namespace Juvin.Platform.Linux.Launcher;

/// <summary>
///     GNOME takes a window's dock icon only from an installed desktop entry whose
///     <c>StartupWMClass</c> matches, so an AppImage writes its own entry and icons into the user's
///     data directory. The deb and rpm install theirs under <c>/usr/share</c> as <c>juvin.desktop</c>;
///     the AppImage entry has its own id so a user entry never shadows a packaged launcher.
/// </summary>
public sealed class AppImageLauncherIntegration : ILauncherIntegration
{
    private const string EntryTemplate = """
        [Desktop Entry]
        Type=Application
        Name=Juvin
        GenericName=Email writing assistant
        Comment=Works beside you
        Exec={0}
        Icon=juvin
        Terminal=false
        Categories=Office;
        StartupNotify=false
        StartupWMClass=juvin
        Keywords=email;rewrite;assistant;

        """;

    private readonly Func<string, string?> _getEnvironment;
    private readonly string? _processPath;
    private readonly string _dataHome;

    /// <param name="getEnvironment">Reads <c>APPDIR</c> and <c>APPIMAGE</c>.</param>
    /// <param name="processPath">The running executable, which must sit under <c>APPDIR</c>.</param>
    /// <param name="dataHome"><c>$XDG_DATA_HOME</c>, or <c>~/.local/share</c>.</param>
    public AppImageLauncherIntegration(Func<string, string?> getEnvironment, string? processPath, string dataHome)
    {
        _getEnvironment = getEnvironment;
        _processPath = processPath;
        _dataHome = dataHome;
    }

    public Task EnsureInstalledAsync(CancellationToken ct) =>
        LinuxProcessPath.RunsFromAppImage(_getEnvironment, _processPath) ? Task.Run(Install, ct) : Task.CompletedTask;

    private void Install()
    {
        var appImage = _getEnvironment("APPIMAGE")!;
        if (appImage.Contains('\n') || appImage.Contains('\r'))
        {
            Trace.WriteLine("[Launcher] The AppImage path contains a line break; skipped the desktop entry.");
            return;
        }

        try
        {
            CopyIcons(Path.Join(_getEnvironment("APPDIR")!, "usr", "share", "icons", "hicolor"), Path.Join(_dataHome, "icons", "hicolor"));

            var applications = Path.Join(_dataHome, "applications");
            var entryPath = Path.Join(applications, "juvin-appimage.desktop");
            var entry = string.Format(CultureInfo.InvariantCulture, EntryTemplate, QuoteExec(appImage));
            if (File.Exists(entryPath) && File.ReadAllText(entryPath) == entry)
            {
                return;
            }

            Directory.CreateDirectory(applications);
            AtomicFile.WriteAllText(entryPath, entry);
            Trace.WriteLine("[Launcher] Wrote the desktop entry.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[Launcher] Could not install the desktop entry: {ex.GetType().Name}");
        }
    }

    private static void CopyIcons(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        var copied = false;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Join(target, Path.GetRelativePath(source, file));
            var sourceInfo = new FileInfo(file);
            var targetInfo = new FileInfo(destination);
            if (targetInfo.Exists && targetInfo.Length == sourceInfo.Length && targetInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
            copied = true;
        }

        // GTK rescans a theme only when its root directory's time changes.
        if (copied)
        {
            Directory.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
    }

    /// <summary>
    ///     Quotes a path as one Exec argument: inside quotes, <c>"</c>, <c>`</c>, <c>$</c>, and
    ///     <c>\</c> take a backslash, then the value's own escaping doubles every backslash again.
    ///     A literal <c>%</c> is written <c>%%</c>.
    /// </summary>
    private static string QuoteExec(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            switch (c)
            {
                case '"' or '`' or '$':
                    quoted.Append(@"\\").Append(c);
                    break;
                case '\\':
                    quoted.Append(@"\\\\");
                    break;
                case '%':
                    quoted.Append("%%");
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
