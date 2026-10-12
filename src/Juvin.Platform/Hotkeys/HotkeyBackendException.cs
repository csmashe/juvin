namespace Juvin.Platform.Hotkeys;

/// <summary>A hotkey backend could not start. The message is plain English shown to the user and carries no key names.</summary>
public sealed class HotkeyBackendException(string message) : Exception(message);
