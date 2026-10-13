namespace Juvin.Platform;

public sealed class NoLauncherIntegration : ILauncherIntegration
{
    public Task EnsureInstalledAsync(CancellationToken ct) => Task.CompletedTask;
}
