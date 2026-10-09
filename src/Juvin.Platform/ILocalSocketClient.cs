// ReSharper disable UnusedMemberInSuper.Global -- part of the platform contract implemented per OS; callers arrive with the native host

namespace Juvin.Platform;

public interface ILocalSocketClient
{
    /// <summary>
    ///     Sends one request line and returns the reply line, or null when no server answers within
    ///     <paramref name="timeout" /> or the reply is not a single line within the size cap.
    /// </summary>
    Task<string?> SendAsync(string line, TimeSpan timeout);

    /// <summary>Whether a running instance is listening on the endpoint.</summary>
    bool IsLivePeer();
}
