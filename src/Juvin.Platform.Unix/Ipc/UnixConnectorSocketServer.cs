namespace Juvin.Platform.Unix.Ipc;

/// <summary>Unix domain socket server for the browser connector endpoint, next to the control socket.</summary>
public sealed class UnixConnectorSocketServer : IConnectorSocketServer, IDisposable
{
    private readonly UnixSocketListener _listener;

    public UnixConnectorSocketServer()
        : this(SocketPathResolver.ResolveConnectorSocketPath())
    {
    }

    public UnixConnectorSocketServer(string socketPath)
    {
        _listener = new UnixSocketListener(socketPath, "connector socket", guard: null);
    }

    public string Address => _listener.Address;

    public Task StartAsync(Func<Stream, CancellationToken, Task> handleConnection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handleConnection);

        return _listener.StartAsync(handleConnection, ct);
    }

    public Task StopAsync() => _listener.StopAsync();

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
