using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Juvin.Platform.Windows;

/// <summary>
///     The Codex installer's own directory, so its binary wins over a shim of the same name, the PATH
///     saved in the registry, which an installer such as Codex's or npm's updates while the app keeps the
///     PATH it started with, then the Codex installer's and npm's default directories.
/// </summary>
public sealed class WindowsToolSearchPaths : IToolSearchPaths
{
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserEnvironmentKey = "Environment";

    private readonly string _appData;
    private readonly string? _codexBin;
    private readonly string _localAppData;
    private readonly RegistryKey _machineRoot;
    private readonly RegistryKey _userRoot;

    public WindowsToolSearchPaths()
        : this(
            Registry.LocalMachine,
            Registry.CurrentUser,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            CodexStandaloneInstall.CurrentBinDirectory()
        )
    {
    }

    /// <param name="machineRoot">Stands in for <c>HKEY_LOCAL_MACHINE</c>.</param>
    /// <param name="userRoot">Stands in for <c>HKEY_CURRENT_USER</c>.</param>
    /// <param name="localAppData">Stands in for the local application data folder.</param>
    /// <param name="appData">Stands in for the roaming application data folder.</param>
    /// <param name="codexBin">Stands in for the Codex installer's own directory.</param>
    internal WindowsToolSearchPaths(RegistryKey machineRoot, RegistryKey userRoot, string localAppData, string appData, string? codexBin)
    {
        _machineRoot = machineRoot;
        _userRoot = userRoot;
        _localAppData = localAppData;
        _appData = appData;
        _codexBin = codexBin;
    }

    /// <summary>Machine entries before user ones, the order Windows gives a new process.</summary>
    public IReadOnlyList<string> GetDirectories() =>
        [
            .. _codexBin is null ? [] : new[] { _codexBin },
            .. SavedPath(_machineRoot, MachineEnvironmentKey),
            .. SavedPath(_userRoot, UserEnvironmentKey),
            .. InstallDirectories(_localAppData, _appData),
        ];

    /// <summary>The Codex installer's default folder for <c>codex.exe</c>, then npm's global folder for <c>codex.cmd</c>.</summary>
    internal static IReadOnlyList<string> InstallDirectories(string localAppData, string appData) =>
        [
            .. string.IsNullOrEmpty(localAppData) ? [] : new[] { Path.Join(localAppData, "Programs", "OpenAI", "Codex", "bin") },
            .. string.IsNullOrEmpty(appData) ? [] : new[] { Path.Join(appData, "npm") },
        ];

    /// <summary>The <c>Path</c> value under <paramref name="subKey" />, with its variables expanded.</summary>
    private static IEnumerable<string> SavedPath(RegistryKey root, string subKey)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue("Path") is string path
                ? path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(entry => entry.Trim('"'))
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Trace.WriteLine($"[ToolSearchPaths] Could not read the saved PATH: {ex.GetType().Name}");
            return [];
        }
    }
}
