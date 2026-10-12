using Juvin.Core.Hotkeys;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>The shortcut a writer registers with the desktop.</summary>
/// <param name="Id">Stable id; writers derive their entry's path or file name from it.</param>
/// <param name="DisplayName">Shown in the desktop's own shortcut settings.</param>
/// <param name="Chord">The keys; each writer formats them the way its desktop expects.</param>
/// <param name="Command">Shell-quoted command line the desktop runs on the press.</param>
public sealed record DesktopShortcutSpec(string Id, string DisplayName, KeyChord Chord, string Command);

/// <summary>
///     Registers a shortcut with one desktop's own keyboard settings, which then runs a command on
///     the press. Failures throw <see cref="IOException" />, <see cref="InvalidOperationException" />,
///     or <see cref="System.ComponentModel.Win32Exception" /> with a technical message.
/// </summary>
public interface IDesktopShortcutWriter
{
    /// <summary>"gnome", "kde", "hyprland", or "sway".</summary>
    string DesktopId { get; }

    /// <summary>True when a written shortcut only takes effect after the user signs in again.</summary>
    bool RequiresSessionRestartToApply { get; }

    /// <summary>Environment and binary lookups only; never spawns a process.</summary>
    bool IsCurrentDesktop();

    /// <summary>True when the registered entry matches <paramref name="spec" /> exactly.</summary>
    Task<bool> IsInstalledAsync(DesktopShortcutSpec spec, CancellationToken ct);

    /// <summary>Adds or updates the entry, leaving the user's other shortcuts alone. Idempotent.</summary>
    Task WriteAsync(DesktopShortcutSpec spec, CancellationToken ct);

    /// <summary>Removes the entry if present.</summary>
    Task RemoveAsync(string id, CancellationToken ct);
}
