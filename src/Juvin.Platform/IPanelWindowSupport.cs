namespace Juvin.Platform;

/// <summary>Window manager hints the rewrite panel needs beyond what Avalonia sets itself.</summary>
public interface IPanelWindowSupport
{
    /// <summary>
    ///     Sets hints that must be in place before the window is first shown, such as where the window
    ///     manager may place it. Best-effort, like <see cref="ShowOnAllWorkspaces" />.
    /// </summary>
    /// <param name="windowHandle">The native window handle Avalonia reports for the panel.</param>
    /// <param name="handleDescriptor">What kind of handle <paramref name="windowHandle" /> is.</param>
    void PrepareWindow(nint windowHandle, string? handleDescriptor);

    /// <summary>
    ///     Asks the window manager to show the window on every workspace, so the panel appears on
    ///     the one the user is on rather than the one it was first shown on. Best-effort: does
    ///     nothing where the platform has no such hint or the request fails.
    /// </summary>
    /// <param name="windowHandle">The native window handle Avalonia reports for the panel.</param>
    /// <param name="handleDescriptor">What kind of handle <paramref name="windowHandle" /> is, such as <c>XID</c> or <c>HWND</c>.</param>
    /// <param name="title">The panel's window title, for compositors that list windows by class and title only.</param>
    void ShowOnAllWorkspaces(nint windowHandle, string? handleDescriptor, string title);

    /// <summary>
    ///     Called before every show, while the app the user is in is still in front. Remembers that app
    ///     for <see cref="Hide" /> and lets the panel show over it, including over a full-screen app.
    /// </summary>
    void BeforeShow();

    /// <summary>
    ///     Hides the panel through <paramref name="hideWindow" /> and, if the panel still has focus, gives it
    ///     back to the app remembered by <see cref="BeforeShow" />, so an Undo in that app works without a click.
    /// </summary>
    /// <param name="hideWindow">Hides the panel window.</param>
    void Hide(Action hideWindow);
}
