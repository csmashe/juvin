namespace Juvin.Platform.Hotkeys;

/// <summary>
///     Lets a setup task re-run backend selection after it grants an access the preferred backend
///     needs, without the platform package knowing the app's hotkey service.
/// </summary>
public interface IHotkeyRestart
{
    Task RestartAsync(CancellationToken ct);
}
