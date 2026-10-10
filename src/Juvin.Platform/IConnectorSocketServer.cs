namespace Juvin.Platform;

/// <summary>
///     The app's browser connector endpoint: a Unix domain socket or a named pipe, reachable only by
///     the current user. Unlike the control socket, connections are long-lived and two-way; each one
///     is a native host relaying one browser session.
/// </summary>
public interface IConnectorSocketServer
{
    /// <summary>Socket path or pipe name clients connect to.</summary>
    string Address { get; }

    /// <summary>
    ///     Binds the endpoint and starts accepting connections. Each connection's stream is passed to
    ///     <paramref name="handleConnection" /> and closed when the handler returns. The handler's
    ///     token fires when the server stops; there is no per-connection timeout.
    ///     Throws <see cref="IOException" /> when another instance owns the endpoint.
    /// </summary>
    Task StartAsync(Func<Stream, CancellationToken, Task> handleConnection, CancellationToken ct);

    Task StopAsync();
}
