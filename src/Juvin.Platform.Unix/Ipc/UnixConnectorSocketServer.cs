namespace Juvin.Platform.Unix.Ipc;

/// <summary>
///     Unix domain socket server for the browser connector endpoint, next to the control socket,
///     plus any per-sandbox addresses added after start.
/// </summary>
public sealed class UnixConnectorSocketServer : IConnectorSocketServer, IDisposable
{
    private const string Description = "connector socket";

    private readonly UnixSocketListener _listener;
    private readonly List<UnixSocketListener> _extraListeners = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Func<Stream, CancellationToken, Task>? _handler;

    public UnixConnectorSocketServer()
        : this(SocketPathResolver.ResolveConnectorSocketPath())
    {
    }

    public UnixConnectorSocketServer(string socketPath)
    {
        _listener = new UnixSocketListener(socketPath, Description, guard: null);
    }

    public string Address => _listener.Address;

    public async Task StartAsync(Func<Stream, CancellationToken, Task> handleConnection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handleConnection);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _listener.StartAsync(handleConnection, ct).ConfigureAwait(false);
            _handler = handleConnection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <remarks>
    ///     The new address lives until <see cref="StopAsync" /> or until <paramref name="ct" /> fires,
    ///     the same as the main address and the token given to <see cref="StartAsync" />.
    /// </remarks>
    public async Task AddAddressAsync(string address, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_handler is null)
            {
                throw new InvalidOperationException("The connector socket must be started before adding addresses.");
            }

            var fullAddress = Path.GetFullPath(address);
            if (fullAddress == _listener.Address || _extraListeners.Any(l => l.Address == fullAddress))
            {
                return;
            }

            SocketPathResolver.PreparePrivateDirectory(Path.GetDirectoryName(fullAddress)!);
            var listener = new UnixSocketListener(fullAddress, Description, guard: null);
            await listener.StartAsync(_handler, ct).ConfigureAwait(false);
            _extraListeners.Add(listener);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _handler = null;
            foreach (var listener in _extraListeners)
            {
                await listener.StopAsync().ConfigureAwait(false);
            }

            _extraListeners.Clear();
            await _listener.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
