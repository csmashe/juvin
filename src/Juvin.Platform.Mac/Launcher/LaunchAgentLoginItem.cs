using System.Diagnostics;
using System.Globalization;
using System.Security;
using Juvin.Core.Services;

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
            {3}
            <true/>
        </dict>
        </plist>

        """;

    private readonly string? _processPath;
    private readonly string _plistPath;

    public LaunchAgentLoginItem()
        : this(Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    internal LaunchAgentLoginItem(string? processPath, string home)
    {
        _processPath = processPath;
        _plistPath = Path.Join(home, "Library", "LaunchAgents", Label + ".plist");
    }

    /// <remarks>Under the <c>dotnet</c> host the agent would start <c>dotnet --background</c>, which is not the app.</remarks>
    public bool IsSupported =>
        _processPath is not null && !string.Equals(Path.GetFileNameWithoutExtension(_processPath), "dotnet", StringComparison.Ordinal);

    public Task EnableAsync(CancellationToken ct) => Task.Run(Enable, ct);

    public Task DisableAsync(CancellationToken ct) => Task.Run(Disable, ct);

    private bool IsOurs() => File.Exists(_plistPath) && File.ReadAllText(_plistPath).Contains(ManagedKey, StringComparison.Ordinal);

    private void Enable()
    {
        var executable = _processPath ?? throw new LoginItemException("The app's executable path is unknown.");
        var plist = string.Format(
            CultureInfo.InvariantCulture,
            PlistTemplate,
            Label,
            SecurityElement.Escape(executable),
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
