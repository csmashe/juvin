namespace Juvin.Platform.Hotkeys;

/// <summary>A hotkey backend could not start. The message is plain English shown to the user and carries no key names.</summary>
/// <param name="needsPermission">The OS refused the backend a permission, such as Accessibility on macOS.</param>
public sealed class HotkeyBackendException(string message, bool needsPermission = false) : Exception(message)
{
    public bool NeedsPermission { get; } = needsPermission;
}
