namespace Juvin.Platform;

/// <summary>The one way to end the app, for the tray, General, and platform code, without knowing its lifetime.</summary>
public interface IAppExit
{
    /// <summary>Starts a normal exit on the UI thread and returns at once.</summary>
    void RequestExit();
}
