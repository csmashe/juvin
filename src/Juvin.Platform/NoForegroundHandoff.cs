namespace Juvin.Platform;

/// <summary>
///     Linux and macOS: an app raises its own window without the app in front allowing it, or, on Wayland,
///     cannot be allowed to at all, so the wizard tells the user to switch.
/// </summary>
public sealed class NoForegroundHandoff : IForegroundHandoff
{
    public void AllowOtherAppToComeForward()
    {
    }
}
