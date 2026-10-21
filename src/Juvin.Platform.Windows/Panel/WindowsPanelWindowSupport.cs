namespace Juvin.Platform.Windows.Panel;

/// <summary>
///     Does nothing beyond hiding: Windows has no public API that pins a window to every virtual desktop, and
///     hiding the active panel already activates the window below it, the browser the capture came from.
/// </summary>
public sealed class WindowsPanelWindowSupport : IPanelWindowSupport
{
    public void PrepareWindow(nint windowHandle, string? handleDescriptor)
    {
    }

    public void ShowOnAllWorkspaces(nint windowHandle, string? handleDescriptor, string title)
    {
    }

    public void BeforeShow()
    {
    }

    public void Hide(Action hideWindow) => hideWindow();
}
