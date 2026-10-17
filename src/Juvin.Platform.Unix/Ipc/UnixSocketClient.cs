using System.Diagnostics;
using System.Net.Sockets;

namespace Juvin.Platform.Unix.Ipc;

/// <summary>Client side of <see cref="UnixSocketServer" />: one request line, one reply line per connection.</summary>
public sealed class UnixSocketClient : ILocalSocketClient
{
    private readonly string _socketPath;

    public UnixSocketClient()
        : this(SocketPathResolver.ResolveControlSocketPath())
    {
    }

    public UnixSocketClient(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        _socketPath = socketPath;
    }

    public async Task<string?> SendAsync(string line, TimeSpan timeout)
    {
        LocalSocketProtocol.ThrowIfInvalidRequest(line);

        if (SocketPathResolver.IsTooLong(_socketPath))
        {
            Trace.WriteLine("[UnixSocketClient] The control socket path is over the length limit.");
            return null;
        }

        if (!File.Exists(_socketPath))
        {
            return null;
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket
                .ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cts.Token)
                .ConfigureAwait(false);
            if (!UnixPeerCredentials.IsOwnedByEffectiveUser(socket))
            {
                Trace.WriteLine("[UnixSocketClient] Refused a control socket served by another user.");
                return null;
            }

            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await LocalSocketProtocol.WriteLineAsync(stream, line, cts.Token).ConfigureAwait(false);
            var (reply, _) = await LocalSocketProtocol
                .ReadLineAsync(stream, LocalSocketProtocol.MaxLineBytes, cts.Token)
                .ConfigureAwait(false);
            return reply;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // ECONNREFUSED alone isn't proof of staleness; re-probe under the ownership lock before unlinking.
            ControlSocketOwnership.TryCleanupStaleSocket(_socketPath);
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return null;
        }
    }
}
