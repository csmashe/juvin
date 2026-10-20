namespace Juvin.Platform.Hotkeys;

/// <summary>
///     Lets a setup task re-run backend selection after it grants an access the preferred backend
///     needs, without the platform package knowing the app's hotkey service.
/// </summary>
public interface IHotkeyRestart
{
    /// <summary>Whether a backend is listening for the shortcut right now.</summary>
    bool IsListening { get; }

    /// <summary>Whether the last start failed because the OS refused a permission, rather than for another reason.</summary>
    bool NeedsPermission { get; }

    Task RestartAsync(CancellationToken ct);
}
