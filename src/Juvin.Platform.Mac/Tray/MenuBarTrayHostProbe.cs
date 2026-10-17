namespace Juvin.Platform.Mac.Tray;

/// <summary>The macOS menu bar always hosts status items.</summary>
public sealed class MenuBarTrayHostProbe : ITrayHostProbe
{
    public Task<bool?> IsTrayHostAvailableAsync(CancellationToken ct) => Task.FromResult<bool?>(true);
}
