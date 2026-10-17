namespace Juvin.Platform.Windows.Tray;

/// <summary>The Windows taskbar notification area always hosts tray icons.</summary>
public sealed class NotificationAreaTrayHostProbe : ITrayHostProbe
{
    public Task<bool?> IsTrayHostAvailableAsync(CancellationToken ct) => Task.FromResult<bool?>(true);
}
