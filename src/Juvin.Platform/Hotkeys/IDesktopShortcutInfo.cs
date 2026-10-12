namespace Juvin.Platform.Hotkeys;

/// <summary>
///     Extra surface of a hotkey backend that registers the shortcut with the desktop environment
///     instead of listening itself. The Shortcuts section reads it without referencing an OS package.
/// </summary>
public interface IDesktopShortcutInfo
{
    /// <summary>True when the written shortcut only fires after the user signs in again.</summary>
    bool RequiresSessionRestartToApply { get; }

    /// <summary>Removes the registered shortcut, leaving the user's other shortcuts intact.</summary>
    Task RemoveAsync(CancellationToken ct);
}
