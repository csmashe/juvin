namespace Juvin.Platform.Tests;

public sealed class ActiveConnectionsTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TryStart_DisposesTheTransportOnceTheCapIsReached()
    {
        var connections = new ActiveConnections(limit: 1);
        var release = new TaskCompletionSource();
        var first = new Transport();
        var second = new Transport();

        Assert.True(connections.TryStart(first, () => release.Task));
        Assert.False(connections.TryStart(second, () => Task.CompletedTask));

        Assert.False(first.IsDisposed);
        Assert.True(second.IsDisposed);
        release.SetResult();
        await connections.CloseAllAsync(s_timeout);
    }

    [Fact]
    public async Task CloseAll_DisposesTransportsWhoseHandlerIsStillRunning()
    {
        var connections = new ActiveConnections();
        var release = new TaskCompletionSource();
        var stuck = new Transport();
        connections.TryStart(stuck, () => release.Task);

        await connections.CloseAllAsync(TimeSpan.FromMilliseconds(100));

        Assert.True(stuck.IsDisposed);
        release.SetResult();
    }

    private sealed class Transport : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
