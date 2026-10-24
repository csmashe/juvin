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
        Stream? stream = null;
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cts.Token).ConfigureAwait(false);
            if (UnixPeerCredentials.IsOwnedByEffectiveUser(socket))
            {
                stream = new NetworkStream(socket, ownsSocket: true);
                return stream;
            }

            Trace.WriteLine("[UnixConnectorSocketClient] Refused a connector socket served by another user.");
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (stream is null)
            {
                socket.Dispose();
            }
        }
    }
}
