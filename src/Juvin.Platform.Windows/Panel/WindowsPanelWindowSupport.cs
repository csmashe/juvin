namespace Juvin.Platform.Windows.Panel;

/// <summary>Does nothing: Windows has no public API that pins a window to every virtual desktop.</summary>
public sealed class WindowsPanelWindowSupport : IPanelWindowSupport
{
    public void ShowOnAllWorkspaces(nint windowHandle)
    {
    }
}
