namespace Juvin.Platform;

/// <summary>Window manager hints the rewrite panel needs beyond what Avalonia sets itself.</summary>
public interface IPanelWindowSupport
{
    /// <summary>
    ///     Asks the window manager to show the window on every workspace, so the panel appears on
    ///     the one the user is on rather than the one it was first shown on. Best-effort: does
    ///     nothing where the platform has no such hint or the request fails.
    /// </summary>
    /// <param name="windowHandle">The native window handle Avalonia reports for the panel.</param>
    void ShowOnAllWorkspaces(nint windowHandle);
}
