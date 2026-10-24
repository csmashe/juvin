namespace Juvin.Platform;

/// <summary>
///     Lets another app come to the front when Juvin asks it to, such as the browser opening the sample
///     page from the setup wizard. Only Windows requires the app in front to allow it; elsewhere this does nothing.
/// </summary>
public interface IForegroundHandoff
{
    /// <summary>Call while Juvin is in front, just before asking the other app to raise its window.</summary>
    void AllowOtherAppToComeForward();
}
