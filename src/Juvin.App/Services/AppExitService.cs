using Juvin.Platform;

namespace Juvin.App.Services;

/// <summary>Forwards exit requests to the app, which runs the normal shutdown on the UI thread.</summary>
public sealed class AppExitService : IAppExit
{
    public event Action? ExitRequested;

    public void RequestExit() => ExitRequested?.Invoke();
}
