namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>The shortcut is written but the compositor did not reload, so it is not live until a reload or restart.</summary>
public sealed class DesktopReloadException(string reloadCommand)
    : InvalidOperationException($"The shortcut is saved, but '{reloadCommand} reload' failed. Run it yourself, or it applies when the desktop restarts.");
