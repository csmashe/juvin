namespace Juvin.Platform;

/// <summary>
///     Installs or refreshes the launcher entry the desktop needs to show the app's icon when the
///     app runs from a location the OS did not install, such as an AppImage. A no-op elsewhere.
/// </summary>
public interface ILauncherIntegration
{
    Task EnsureInstalledAsync(CancellationToken ct);
}
