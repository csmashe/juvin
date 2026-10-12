namespace Juvin.Platform.Linux.Hotkeys.Evdev;

/// <summary>
///     Whether this process can open a keyboard node right now. This, not group membership, is the
///     real test: the <c>uaccess</c> udev rule grants access through a session ACL without the
///     <c>input</c> group.
/// </summary>
public static class InputDeviceAccessCheck
{
    public static bool HasKeyboardAccess() => OperatingSystem.IsLinux() && KeyboardDeviceDiscovery.EnumerateKeyboards().Count > 0;
}
