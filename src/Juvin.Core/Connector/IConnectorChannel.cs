namespace Juvin.Core.Connector;

/// <summary>One connector connection carrying whole JSON messages. Disposing it closes the connection.</summary>
public interface IConnectorChannel : IAsyncDisposable
{
    /// <summary>Sends one message. Safe to call concurrently with itself and with <see cref="ReceiveAsync" />.</summary>
    Task SendAsync(string json, CancellationToken ct);

    /// <summary>Returns the next message, or null when the peer closed the connection.</summary>
    /// <exception cref="ConnectorFrameException">The peer broke the framing rules.</exception>
    Task<string?> ReceiveAsync(CancellationToken ct);
}
