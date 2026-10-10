using System.Net.Sockets;

namespace Juvin.Platform.Unix.Ipc;

/// <summary>Client side of <see cref="UnixConnectorSocketServer" />, used by the native host.</summary>
public sealed class UnixConnectorSocketClient : IConnectorSocketClient
{
    private readonly string _socketPath;

    public UnixConnectorSocketClient()
        : this(SocketPathResolver.ResolveConnectorClientPath())
    {
    }

    public UnixConnectorSocketClient(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        _socketPath = socketPath;
    }

    public async Task<Stream?> ConnectAsync(TimeSpan timeout)
    {
        if (!File.Exists(_socketPath))
        {
            return null;
        }

        using var cts = new CancellationTokenSource(timeout);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cts.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            socket.Dispose();
            return null;
        }
    }
}
