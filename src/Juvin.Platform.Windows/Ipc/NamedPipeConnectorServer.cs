namespace Juvin.Platform.Windows.Ipc;

/// <summary>Named pipe server for the browser connector endpoint.</summary>
public sealed class NamedPipeConnectorServer : IConnectorSocketServer, IDisposable
{
    /// <summary>
    ///     A fraction of the 1 MiB frame cap. Larger frames still pass because both ends read
    ///     continuously, so a write that fills the buffer only waits for the reader.
    /// </summary>
    private const int BufferBytes = 64 * 1024;

    private readonly NamedPipeListener _listener;

    public NamedPipeConnectorServer()
        : this(PipeNames.Connector)
    {
    }

    public NamedPipeConnectorServer(string pipeName)
    {
        _listener = new NamedPipeListener(pipeName, "connector pipe", BufferBytes);
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
