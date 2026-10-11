namespace Juvin.Platform.Mac.Panel;

/// <summary>Does nothing: how a floating window behaves across Spaces is unverified until macOS testing.</summary>
public sealed class MacPanelWindowSupport : IPanelWindowSupport
{
    public void ShowOnAllWorkspaces(nint windowHandle)
    {
    }
}
