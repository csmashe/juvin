namespace Juvin.Platform;

/// <summary>
///     Reports whether the desktop shows tray icons. Hiding the window to a tray that nobody
///     displays would leave the user with no way back to the app.
/// </summary>
public interface ITrayHostProbe
{
    /// <summary>
    ///     True or false when the desktop answered; null when it could not tell, such as a busy
    ///     session bus, so the caller can keep its last answer.
    /// </summary>
    Task<bool?> IsTrayHostAvailableAsync(CancellationToken ct);
}
