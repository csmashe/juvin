using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Juvin.Platform.Windows.Launcher;

/// <summary>
///     A <c>Juvin</c> value under the per-user <c>Run</c> key, which Windows starts at sign-in. Enabling
///     overwrites whatever value is there; disabling removes only a value that names this executable.
/// </summary>
public sealed class RunKeyLoginItem : ILoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Juvin";

    private readonly string? _processPath;
    private readonly RegistryKey _userRoot;

    public RunKeyLoginItem()
        : this(Environment.ProcessPath, Registry.CurrentUser)
    {
    }

    /// <param name="processPath">The running executable.</param>
    /// <param name="userRoot">Stands in for <c>HKEY_CURRENT_USER</c>.</param>
    internal RunKeyLoginItem(string? processPath, RegistryKey userRoot)
    {
        _processPath = processPath;
        _userRoot = userRoot;
    }

    public bool IsSupported => _processPath is not null;

    private string? QuotedExecutable => _processPath is null ? null : "\"" + _processPath + "\"";

    public Task EnableAsync(CancellationToken ct) => Task.Run(Enable, ct);

    public Task DisableAsync(CancellationToken ct) => Task.Run(Disable, ct);

    private void Enable()
    {
        var executable = QuotedExecutable ?? throw new LoginItemException("The app's executable path is unknown.");
        try
        {
            using var key = _userRoot.CreateSubKey(RunKey);
            key.SetValue(ValueName, executable + " " + ILoginItem.BackgroundArgument, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new LoginItemException($"Could not write the Run value: {ex.Message}", ex);
        }

        Trace.WriteLine("[RunKey] Wrote the Run value.");
    }

    private void Disable()
    {
        try
        {
            if (!IsOurs())
            {
                return;
            }

            using var key = _userRoot.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new LoginItemException($"Could not remove the Run value: {ex.Message}", ex);
        }

        Trace.WriteLine("[RunKey] Removed the Run value.");
    }

    private bool IsOurs()
    {
        if (QuotedExecutable is not { } executable)
        {
            return false;
        }

        using var key = _userRoot.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.StartsWith(executable, StringComparison.OrdinalIgnoreCase);
    }
}
