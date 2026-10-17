using System.Diagnostics;
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
        if (SocketPathResolver.IsTooLong(_socketPath))
        {
            Trace.WriteLine("[UnixConnectorSocketClient] The connector socket path is over the length limit.");
            return null;
        }

        if (!File.Exists(_socketPath))
        {
            return null;
        }

        using var cts = new CancellationTokenSource(timeout);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cts.Token).ConfigureAwait(false);
            if (UnixPeerCredentials.IsOwnedByEffectiveUser(socket))
            {
                return new NetworkStream(socket, ownsSocket: true);
            }

            Trace.WriteLine("[UnixConnectorSocketClient] Refused a connector socket served by another user.");
            socket.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            socket.Dispose();
            return null;
        }
    }
}
