using System.Net;
using System.Net.Sockets;

namespace Juvin.Core.Providers;

/// <summary>The addresses Local mode may send a draft to: this machine or the user's own network.</summary>
internal static class LocalNetwork
{
    /// <summary>Loopback, a private or carrier-grade NAT range, or link-local.</summary>
    public static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return IPAddress.IsLoopback(address) || address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            127 or 10 => true,
            // Carrier-grade NAT, which is also where Tailscale puts its devices.
            100 => bytes[1] is >= 64 and <= 127,
            172 => bytes[1] is >= 16 and <= 31,
            192 => bytes[1] == 168,
            169 => bytes[1] == 254,
            _ => false,
        };
    }
}

/// <summary>Thrown at connect time when the server's host resolves only to addresses outside the local network.</summary>
internal sealed class OutsideLocalNetworkException()
    : HttpRequestException("The local model server address resolves to an address outside this machine and your local network.");
