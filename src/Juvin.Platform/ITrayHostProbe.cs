namespace Juvin.Platform;

/// <summary>
///     Reports whether the desktop shows tray icons. Hiding the window to a tray that nobody
///     displays would leave the user with no way back to the app.
/// </summary>
public interface ITrayHostProbe
{
    bool IsTrayHostAvailable();
}
