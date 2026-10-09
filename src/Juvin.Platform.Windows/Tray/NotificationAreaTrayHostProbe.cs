namespace Juvin.Platform.Windows.Tray;

/// <summary>The Windows taskbar notification area always hosts tray icons.</summary>
public sealed class NotificationAreaTrayHostProbe : ITrayHostProbe
{
    public bool IsTrayHostAvailable() => true;
}
