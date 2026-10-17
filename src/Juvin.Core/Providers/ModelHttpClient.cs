using System.Net;
using System.Net.Sockets;

namespace Juvin.Core.Providers;

/// <summary>
///     The HTTP client for model servers. Redirects are not followed, so a server cannot forward
///     the prompt to another host; a 3xx reply fails like any other non-success status. No system
///     proxy is used, and every connection is checked after name resolution, so a request only
///     ever reaches this machine or the user's local network, whatever the host name resolves to.
/// </summary>
public static class ModelHttpClient
{
    public static HttpClient Create(TimeSpan timeout) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = ConnectLocallyAsync })
        {
            Timeout = timeout,
        };

    /// <exception cref="OutsideLocalNetworkException">The host has no address on this machine or the local network.</exception>
    private static async ValueTask<Stream> ConnectLocallyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(LocalNetwork.IsLocalAddress).ToList();
        if (allowed.Count == 0)
        {
            throw new OutsideLocalNetworkException();
        }

        // "localhost" usually resolves to ::1 and 127.0.0.1, and a server may listen on only one.
        SocketException? last = null;
        foreach (var candidate in allowed)
        {
            var address = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last!;
    }
}
