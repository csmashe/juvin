namespace Juvin.Platform;

/// <summary>The native host's side of <see cref="IConnectorSocketServer" />.</summary>
public interface IConnectorSocketClient
{
    /// <summary>
    ///     Connects to the running app and returns the connection's stream, or null when nothing
    ///     accepts within <paramref name="timeout" />.
    /// </summary>
    Task<Stream?> ConnectAsync(TimeSpan timeout);
}
