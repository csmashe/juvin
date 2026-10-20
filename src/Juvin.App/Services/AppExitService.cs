using Juvin.Platform;

namespace Juvin.App.Services;

/// <summary>Forwards exit requests from platform code to the app, which owns the desktop lifetime.</summary>
public sealed class AppExitService : IAppExit
{
    public event Action? ExitRequested;

    public void RequestExit() => ExitRequested?.Invoke();
}
