using System.ComponentModel;
using System.Diagnostics;
using Juvin.Core.Hotkeys;
using Juvin.Core.Localization;
using Juvin.Platform.Hotkeys;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>
///     Registers the shortcut with the desktop's own settings, which run <c>juvin --trigger</c> through
///     the control socket, so <see cref="Pressed" /> never fires. Stopping leaves the shortcut in place so
///     restarts do not churn the user's settings; <see cref="RemoveAsync" /> takes it out.
/// </summary>
public sealed class DesktopShortcutHotkeyBackend : IHotkeyBackend, IDesktopShortcutInfo
{
    private const string BackendId = "desktop-shortcut";
    private const string ShortcutId = "juvin.rewrite";

    private readonly Localizer _localizer;
    // ReSharper disable once ReplaceWithFieldKeyword -- the repo does not use the field keyword yet
    private readonly string? _processPath;
    private readonly Lazy<IDesktopShortcutWriter?> _writer;

    public DesktopShortcutHotkeyBackend(IEnumerable<IDesktopShortcutWriter> writers, Localizer localizer)
        : this(writers, localizer, LinuxProcessPath.Resolve(Environment.GetEnvironmentVariable, Environment.ProcessPath))
    {
    }

    internal DesktopShortcutHotkeyBackend(IEnumerable<IDesktopShortcutWriter> writers, Localizer localizer, string? processPath)
    {
        _localizer = localizer;
        _processPath = processPath;
        _writer = new Lazy<IDesktopShortcutWriter?>(() => writers.FirstOrDefault(writer => writer.IsCurrentDesktop()));
    }

    public string Id => BackendId;

    public bool IsAvailable => OperatingSystem.IsLinux() && _writer.Value is not null;

    /// <summary>The recognised desktop, or an empty string when there is none.</summary>
    public string DesktopId => _writer.Value?.DesktopId ?? "";

    public bool RequiresSessionRestartToApply => _writer.Value?.RequiresSessionRestartToApply ?? false;

    /// <summary>The command line the desktop runs, quoted for a shell and for a desktop entry.</summary>
    private string Command => TriggerCommand(_processPath);

    public event Action? Pressed
    {
        add { }
        remove { }
    }

    public async Task StartAsync(KeyChord chord, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var writer = _writer.Value ?? throw new HotkeyBackendException(_localizer["Hotkey.Linux.NoDesktop"]);
        var spec = new DesktopShortcutSpec(ShortcutId, _localizer["Hotkey.Linux.ShortcutName"], chord, Command);
        try
        {
            if (await writer.IsInstalledAsync(spec, ct).ConfigureAwait(false))
            {
                return;
            }

            await writer.WriteAsync(spec, ct).ConfigureAwait(false);
            Trace.WriteLine($"[DesktopShortcutHotkeyBackend] Wrote the {writer.DesktopId} shortcut.");
        }
        catch (Exception ex) when (IsWriterFailure(ex))
        {
            Trace.WriteLine($"[DesktopShortcutHotkeyBackend] Writing the {writer.DesktopId} shortcut failed ({ex.GetType().Name}).");
            throw new HotkeyBackendException(_localizer.Format("Hotkey.Linux.DesktopShortcutFailed", ex.Message));
        }
    }

    public Task StopAsync() => Task.CompletedTask;

    /// <exception cref="HotkeyBackendException">The desktop's settings could not be changed; the message is shown to the user.</exception>
    public async Task RemoveAsync(CancellationToken ct)
    {
        if (_writer.Value is not { } writer)
        {
            return;
        }

        try
        {
            await writer.RemoveAsync(ShortcutId, ct).ConfigureAwait(false);
            Trace.WriteLine($"[DesktopShortcutHotkeyBackend] Removed the {writer.DesktopId} shortcut.");
        }
        catch (Exception ex) when (IsWriterFailure(ex))
        {
            Trace.WriteLine($"[DesktopShortcutHotkeyBackend] Removing the {writer.DesktopId} shortcut failed ({ex.GetType().Name}).");
            throw new HotkeyBackendException(_localizer.Format("Hotkey.Linux.DesktopShortcutRemoveFailed", ex.Message));
        }
    }

    /// <summary>The running executable plus <c>--trigger</c>; under the <c>dotnet</c> host, the installed <c>juvin</c> on the path.</summary>
    internal static string TriggerCommand(string? processPath)
    {
        var useInstalled = string.IsNullOrEmpty(processPath)
            || string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.Ordinal);
        return (useInstalled ? "juvin" : DesktopTriggers.QuoteArgument(processPath!)) + " --trigger";
    }

    private static bool IsWriterFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or Win32Exception;
}
