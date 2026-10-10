namespace Juvin.Platform.Unix.Ipc;

/// <summary>Unix domain socket server for the control endpoint: one request line and one reply line per connection.</summary>
public sealed class UnixSocketServer : ILocalSocketServer, IDisposable
{
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(30);

    private readonly UnixSocketListener _listener;

    public UnixSocketServer(UnixSingleInstanceGuard? guard = null)
        : this(SocketPathResolver.ResolveControlSocketPath(), guard)
    {
    }

    public UnixSocketServer(string socketPath, UnixSingleInstanceGuard? guard = null)
    {
        _listener = new UnixSocketListener(socketPath, "control socket", guard);
    }

    public string Address => _listener.Address;

    public Task StartAsync(Func<string, CancellationToken, Task<string>> handleLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handleLine);

        return _listener.StartAsync((stream, token) => ServeAsync(stream, handleLine, token), ct);
    }

    public Task StopAsync() => _listener.StopAsync();

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private static async Task ServeAsync(
        Stream stream,
        Func<string, CancellationToken, Task<string>> handleLine,
        CancellationToken ct
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_requestTimeout);
        var reply = await LocalSocketProtocol
            .ReadAndHandleAsync(stream, handleLine, timeout.Token)
            .ConfigureAwait(false);
        if (reply is not null)
        {
            await LocalSocketProtocol.WriteLineAsync(stream, reply, timeout.Token).ConfigureAwait(false);
        }
    }
}
