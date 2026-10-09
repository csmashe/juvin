namespace Juvin.Platform;

/// <summary>
///     The app's local control endpoint: a Unix domain socket or a named pipe, reachable only by
///     the current user. Each connection carries one request line and receives one reply line.
/// </summary>
public interface ILocalSocketServer
{
    /// <summary>Socket path or pipe name clients connect to.</summary>
    string Address { get; }

    /// <summary>
    ///     Binds the endpoint and starts accepting connections. Each request line, without its
    ///     newline, is passed to <paramref name="handleLine" />, whose result is sent back as the
    ///     reply. The handler's token fires when the request times out or the server stops.
    ///     Throws <see cref="IOException" /> when another instance owns the endpoint.
    /// </summary>
    Task StartAsync(Func<string, CancellationToken, Task<string>> handleLine, CancellationToken ct);

    Task StopAsync();
}
