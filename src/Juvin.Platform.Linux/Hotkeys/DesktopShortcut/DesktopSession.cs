using System.ComponentModel;
using System.Diagnostics;
using Juvin.Core.Providers;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>The environment the shortcut writers read; tests point it at a temp directory.</summary>
/// <param name="ConfigHome"><c>$XDG_CONFIG_HOME</c>, or <c>~/.config</c>.</param>
/// <param name="DataHome"><c>$XDG_DATA_HOME</c>, or <c>~/.local/share</c>.</param>
/// <param name="ExecutableDirectories">The <c>PATH</c> entries.</param>
/// <param name="GetEnvironment">Reads a session environment variable.</param>
internal sealed record DesktopSession(
    string ConfigHome,
    string DataHome,
    IReadOnlyList<string> ExecutableDirectories,
    Func<string, string?> GetEnvironment
)
{
    private static readonly TimeSpan s_commandTimeout = TimeSpan.FromSeconds(10);

    public static DesktopSession FromEnvironment()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return new DesktopSession(
            string.IsNullOrEmpty(configHome) ? Path.Join(home, ".config") : configHome,
            string.IsNullOrEmpty(dataHome) ? Path.Join(home, ".local", "share") : dataHome,
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries),
            Environment.GetEnvironmentVariable
        );
    }

    /// <summary>
    ///     "gnome", "kde", "hyprland", "sway", or null. The compositors' own session variables win
    ///     over <c>XDG_CURRENT_DESKTOP</c>, which a nested session may have inherited.
    /// </summary>
    public string? DesktopId
    {
        get
        {
            if (!string.IsNullOrEmpty(GetEnvironment("HYPRLAND_INSTANCE_SIGNATURE")))
            {
                return "hyprland";
            }

            if (!string.IsNullOrEmpty(GetEnvironment("SWAYSOCK")))
            {
                return "sway";
            }

            // Colon-separated, such as "ubuntu:GNOME".
            var desktops = (GetEnvironment("XDG_CURRENT_DESKTOP") ?? "").ToLowerInvariant();
            return desktops switch
            {
                _ when desktops.Contains("hyprland") => "hyprland",
                _ when desktops.Contains("sway") => "sway",
                _ when desktops.Contains("gnome") || desktops.Contains("ubuntu") => "gnome",
                _ when desktops.Contains("kde") || desktops.Contains("plasma") => "kde",
                _ => null,
            };
        }
    }

    public bool HasExecutable(string name) =>
        ExecutableDirectories.Any(directory => File.Exists(Path.Join(directory, name)));

    /// <summary>Runs a desktop tool with a timeout, so a hung session bus cannot hang the app.</summary>
    public static async Task<ProcessResult> RunAsync(IProcessRunner runner, string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_commandTimeout);
        try
        {
            return await runner.RunAsync(fileName, arguments, null, null, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{fileName} did not finish within {s_commandTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    ///     Asks a compositor to reload its already written config. On failure <paramref name="pendingMarker" />
    ///     makes the next write reload even when unchanged, and <see cref="DesktopReloadException" /> tells the user.
    /// </summary>
    public static async Task ReloadAsync(IProcessRunner runner, string fileName, string pendingMarker, CancellationToken ct)
    {
        var exitCode = -1;
        try
        {
            exitCode = (await RunAsync(runner, fileName, ["reload"], ct).ConfigureAwait(false)).ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Trace.WriteLine($"[DesktopSession] {fileName} reload failed ({ex.GetType().Name}).");
        }

        if (exitCode == 0)
        {
            File.Delete(pendingMarker);
            return;
        }

        Trace.WriteLine($"[DesktopSession] {fileName} reload exited with {exitCode}.");
        await File.WriteAllTextAsync(pendingMarker, "", ct).ConfigureAwait(false);
        throw new DesktopReloadException(fileName);
    }
}
