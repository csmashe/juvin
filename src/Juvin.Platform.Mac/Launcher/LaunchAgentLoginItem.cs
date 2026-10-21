using System.Diagnostics;
using System.Globalization;
using System.Security;
using Juvin.Core.Localization;
using Juvin.Core.Services;
using Juvin.Platform.Unix;

namespace Juvin.Platform.Mac.Launcher;

/// <summary>
///     A per-user LaunchAgent in <c>~/Library/LaunchAgents</c>. launchd reads it at the next login,
///     so enabling does not start a second copy now. Enabling overwrites whatever plist is there;
///     disabling removes only a plist with the marker key.
/// </summary>
public sealed class LaunchAgentLoginItem : ILoginItem
{
    private const string Label = "com.juvin.app";
    private const string ManagedKey = "<key>JuvinManaged</key>";

    private const string PlistTemplate = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{0}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{1}</string>
                <string>{2}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>LimitLoadToSessionType</key>
            <string>Aqua</string>
            <key>AbandonProcessGroup</key>
            <true/>
            {3}
            <true/>
        </dict>
        </plist>

        """;

    private readonly string? _processPath;
    private readonly string _plistPath;
    private readonly Localizer _localizer;
    private readonly Func<string, bool> _canWrite;

    public LaunchAgentLoginItem(Localizer localizer)
        : this(Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), localizer, UnixFileAccess.CanWrite)
    {
    }

    internal LaunchAgentLoginItem(string? processPath, string home, Localizer localizer, Func<string, bool> canWrite)
    {
        _processPath = processPath;
        _plistPath = Path.Join(home, "Library", "LaunchAgents", Label + ".plist");
        _localizer = localizer;
        _canWrite = canWrite;
    }

    /// <remarks>
    ///     Only an executable in <c>X.app/Contents/MacOS</c> is the installed app; a Debug apphost or the
    ///     <c>dotnet</c> host would start a build output that is not.
    /// </remarks>
    public bool IsSupported => _processPath is not null && IsInAppBundle(_processPath);

    private static bool IsInAppBundle(string executable) => MacAppRelauncher.BundlePath(executable) is not null;

    public Task EnableAsync(CancellationToken ct) => Task.Run(Enable, ct);

    public Task DisableAsync(CancellationToken ct) => Task.Run(Disable, ct);

    private bool IsOurs() => File.Exists(_plistPath) && File.ReadAllText(_plistPath).Contains(ManagedKey, StringComparison.Ordinal);

    private void Enable()
    {
        if (_processPath is null || !IsInAppBundle(_processPath))
        {
            throw new LoginItemException("The app is not running from an app bundle.");
        }

        // Both paths are gone at the next login: Gatekeeper's temporary copy, and a disk image once it is ejected.
        if (
            _processPath.Contains("/AppTranslocation/", StringComparison.Ordinal)
            || (_processPath.StartsWith("/Volumes/", StringComparison.Ordinal) && !_canWrite(Path.GetDirectoryName(_processPath)!))
        )
        {
            throw new LoginItemException("The app runs from a translocated or read-only location.")
            {
                UserMessage = _localizer["LoginItem.Mac.NotInstalled"],
            };
        }

        var plist = string.Format(
            CultureInfo.InvariantCulture,
            PlistTemplate,
            Label,
            SecurityElement.Escape(_processPath),
            ILoginItem.BackgroundArgument,
            ManagedKey
        );
        try
        {
            if (File.Exists(_plistPath) && File.ReadAllText(_plistPath) == plist)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_plistPath)!);
            AtomicFile.WriteAllText(_plistPath, plist);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LoginItemException($"Could not write the launch agent: {ex.Message}", ex);
        }

        Trace.WriteLine("[LaunchAgent] Wrote the launch agent.");
    }

    private void Disable()
    {
        try
        {
            if (!IsOurs())
            {
                return;
            }

            File.Delete(_plistPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LoginItemException($"Could not remove the launch agent: {ex.Message}", ex);
        }

        Trace.WriteLine("[LaunchAgent] Removed the launch agent.");
    }
}
