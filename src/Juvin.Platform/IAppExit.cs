namespace Juvin.Platform;

/// <summary>Lets platform code end the app the way the menu's Exit does, without knowing the app's lifetime.</summary>
public interface IAppExit
{
    /// <summary>Starts a normal exit on the UI thread and returns at once.</summary>
    void RequestExit();
}
